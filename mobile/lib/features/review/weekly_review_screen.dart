import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_providers.dart';
import '../../core/api/wordos_api.dart';
import '../../core/l10n/app_strings.dart';
import '../../core/models/models.dart';
import '../../core/theme/app_tokens.dart';
import '../../core/widgets/app_widgets.dart';

/// Weekly Review — a queue loop over the words added during the period.
///
/// Wrong answers return to the end of the queue until every word is cleared.
/// It measures retention and **never** changes pipeline state (rule R9).
class WeeklyReviewScreen extends ConsumerStatefulWidget {
  const WeeklyReviewScreen({super.key});

  @override
  ConsumerState<WeeklyReviewScreen> createState() => _WeeklyReviewScreenState();
}

class _WeeklyReviewScreenState extends ConsumerState<WeeklyReviewScreen> {
  WeeklyReviewSession? _session;
  ReviewItem? _current;
  ReviewAnswerResult? _lastResult;
  WeeklyReviewResult? _result;
  ApiException? _error;

  bool _loading = true;
  bool _busy = false;
  int _remaining = 0;
  String? _selected;

  @override
  void initState() {
    super.initState();
    _start();
  }

  Future<void> _start() async {
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      final session = await ref.read(wordOsApiProvider).startWeeklyReview();
      if (!mounted) return;
      setState(() {
        _session = session;
        _current = session.queue.isEmpty ? null : session.queue.first;
        _remaining = session.queue.length;
        _loading = false;
      });
    } on ApiException catch (e) {
      if (!mounted) return;
      setState(() {
        _error = e;
        _loading = false;
      });
    }
  }

  Future<void> _answer(String answer) async {
    final item = _current;
    if (item == null || _busy || _lastResult != null) return;
    setState(() {
      _busy = true;
      _selected = answer;
    });
    try {
      final result = await ref.read(wordOsApiProvider).answerWeeklyReview(
            reviewId: _session!.id,
            itemId: item.id,
            answer: answer,
          );
      if (mounted) {
        setState(() {
          _lastResult = result;
          _remaining = result.remaining;
        });
      }
    } on ApiException catch (e) {
      if (mounted) {
        ScaffoldMessenger.of(context)
            .showSnackBar(SnackBar(content: Text(e.message)));
      }
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _next() async {
    final result = _lastResult;
    if (result == null) return;
    if (result.nextItem == null) {
      setState(() => _busy = true);
      try {
        final summary =
            await ref.read(wordOsApiProvider).completeWeeklyReview(_session!.id);
        if (mounted) setState(() => _result = summary);
      } on ApiException catch (e) {
        if (mounted) {
          ScaffoldMessenger.of(context)
              .showSnackBar(SnackBar(content: Text(e.message)));
        }
      } finally {
        if (mounted) setState(() => _busy = false);
      }
      return;
    }
    setState(() {
      _current = result.nextItem;
      _lastResult = null;
      _selected = null;
    });
  }

  @override
  Widget build(BuildContext context) {
    final s = ref.watch(stringsProvider);
    final color = context.palette.review;

    return Scaffold(
      appBar: AppBar(title: Text(s.weeklyReview)),
      body: SafeArea(child: _body(s, color)),
    );
  }

  Widget _body(AppStrings s, Color color) {
    if (_loading) return BusyView(message: s.loading);

    if (_error != null) {
      return EmptyState(
        icon: Icons.event_available_rounded,
        title: _error!.message,
        message: s.reviewDoesNotChange,
        action: OutlinedButton(
          onPressed: () => Navigator.of(context).maybePop(),
          child: Text(s.backToHub),
        ),
      );
    }

    if (_result != null) return _summary(s, color, _result!);

    final item = _current;
    if (item == null) return BusyView(message: s.loading);

    final total = _session!.totalWords;
    final done = (total - _remaining).clamp(0, total);

    return Column(
      children: [
        Padding(
          padding: const EdgeInsets.all(AppSpacing.md),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  Expanded(
                    child: Text(
                      s.reviewDoesNotChange,
                      style: context.text.bodySmall?.copyWith(
                        color: context.colors.onSurface.withValues(alpha: 0.6),
                      ),
                    ),
                  ),
                  const SizedBox(width: AppSpacing.xs),
                  StatusPill(
                    label: '${s.reviewRemaining} $_remaining',
                    color: color,
                  ),
                ],
              ),
              const SizedBox(height: AppSpacing.sm),
              StepProgressBar(value: total == 0 ? 0 : done / total),
            ],
          ),
        ),
        Expanded(
          child: SingleChildScrollView(
            padding: AppSpacing.page,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                const SizedBox(height: AppSpacing.md),
                AppCard(
                  color: color.withValues(alpha: 0.07),
                  borderColor: color.withValues(alpha: 0.28),
                  padding: const EdgeInsets.symmetric(
                    horizontal: AppSpacing.md,
                    vertical: AppSpacing.xl,
                  ),
                  child: Center(
                    child: Text(
                      item.prompt,
                      textAlign: TextAlign.center,
                      style: context.text.headlineSmall,
                    ),
                  ),
                ),
                const SizedBox(height: AppSpacing.lg),
                for (final option in item.options)
                  OptionTile(
                    label: option,
                    enabled: _lastResult == null && !_busy,
                    correct: _lastResult == null
                        ? null
                        : option == _lastResult!.correctAnswer
                            ? true
                            : (_selected == option ? false : null),
                    onTap: () => _answer(option),
                  ),
                if (_lastResult != null && _lastResult!.requeued) ...[
                  const SizedBox(height: AppSpacing.sm),
                  StatusPill(
                    label: s.reviewRequeued,
                    color: context.palette.warning,
                    icon: Icons.replay_rounded,
                  ),
                ],
              ],
            ),
          ),
        ),
        Padding(
          padding: const EdgeInsets.all(AppSpacing.md),
          child: FilledButton(
            onPressed: _lastResult == null || _busy ? null : _next,
            child: Text(
              _lastResult?.nextItem == null && _lastResult != null
                  ? s.finish
                  : s.next,
            ),
          ),
        ),
      ],
    );
  }

  Widget _summary(AppStrings s, Color color, WeeklyReviewResult result) {
    final percent = (result.weeklyScore * 100).round();
    return Column(
      children: [
        Expanded(
          child: ListView(
            padding: const EdgeInsets.all(AppSpacing.md),
            children: [
              const SizedBox(height: AppSpacing.xl),
              Center(
                child: Column(
                  children: [
                    Container(
                      width: 110,
                      height: 110,
                      alignment: Alignment.center,
                      decoration: BoxDecoration(
                        color: color.withValues(alpha: 0.12),
                        shape: BoxShape.circle,
                      ),
                      child: Text(
                        '$percent%',
                        style: context.text.headlineMedium
                            ?.copyWith(color: color),
                      ),
                    ),
                    const SizedBox(height: AppSpacing.md),
                    Text(s.weeklyScore, style: context.text.titleMedium),
                  ],
                ),
              ),
              const SizedBox(height: AppSpacing.lg),
              AppCard(
                child: Column(
                  children: [
                    _SummaryRow(
                      label: s.firstPassCorrect,
                      value: '${result.firstPassCorrect} / ${result.totalWords}',
                    ),
                    const Divider(height: AppSpacing.lg),
                    _SummaryRow(
                      label: s.reviewRemaining,
                      value: '${result.totalAttempts}',
                    ),
                  ],
                ),
              ),
              const SizedBox(height: AppSpacing.md),
              Text(
                s.reviewDoesNotChange,
                textAlign: TextAlign.center,
                style: context.text.bodySmall?.copyWith(
                  color: context.colors.onSurface.withValues(alpha: 0.6),
                ),
              ),
            ],
          ),
        ),
        Padding(
          padding: const EdgeInsets.all(AppSpacing.md),
          child: FilledButton(
            onPressed: () => Navigator.of(context).maybePop(),
            child: Text(s.backToHub),
          ),
        ),
      ],
    );
  }
}

class _SummaryRow extends StatelessWidget {
  const _SummaryRow({required this.label, required this.value});

  final String label;
  final String value;

  @override
  Widget build(BuildContext context) {
    return Row(
      children: [
        Expanded(child: Text(label, style: context.text.bodyMedium)),
        Text(value, style: context.text.titleSmall),
      ],
    );
  }
}
