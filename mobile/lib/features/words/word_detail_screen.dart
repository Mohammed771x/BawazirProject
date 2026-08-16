import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:intl/intl.dart' hide TextDirection;

import '../../core/api/api_providers.dart';
import '../../core/l10n/app_strings.dart';
import '../../core/models/models.dart';
import '../../core/theme/app_tokens.dart';
import '../../core/theme/skill_visuals.dart';
import '../../core/widgets/app_widgets.dart';

final wordDetailProvider =
    FutureProvider.autoDispose.family<WordDetail, String>((ref, id) {
  return ref.watch(wordOsApiProvider).wordDetail(id);
});

/// The word's full journey: five skill states with their schedules, plus the
/// event history that the MVP needs for algorithm validation.
class WordDetailScreen extends ConsumerWidget {
  const WordDetailScreen({super.key, required this.wordId});

  final String wordId;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    final detail = ref.watch(wordDetailProvider(wordId));

    return Scaffold(
      appBar: AppBar(title: Text(s.wordJourney)),
      body: detail.when(
        loading: () => BusyView(message: s.loading),
        error: (e, _) => ErrorView(
          message: s.somethingWentWrong,
          retryLabel: s.retry,
          onRetry: () => ref.invalidate(wordDetailProvider(wordId)),
        ),
        data: (data) {
          final word = data.word;
          final dateFormat = DateFormat.MMMd(s.locale.languageCode);
          return ListView(
            padding: const EdgeInsets.all(AppSpacing.md),
            children: [
              AppCard(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Row(
                      children: [
                        Expanded(
                          child: Text(word.text,
                              style: context.text.headlineSmall),
                        ),
                        LevelBadge(label: word.cefrLevel.label),
                      ],
                    ),
                    const SizedBox(height: AppSpacing.xxs),
                    Text(
                      word.meaning,
                      textDirection: TextDirection.rtl,
                      style: context.text.titleMedium
                          ?.copyWith(color: context.colors.primary),
                    ),
                    if (word.definitionEn.isNotEmpty) ...[
                      const SizedBox(height: AppSpacing.xs),
                      Text(
                        word.definitionEn,
                        style: context.text.bodySmall?.copyWith(
                          color:
                              context.colors.onSurface.withValues(alpha: 0.6),
                        ),
                      ),
                    ],
                    const SizedBox(height: AppSpacing.sm),
                    Row(
                      children: [
                        StatusPill(
                          label: s.stateLabel(word.state),
                          color: word.state == WordState.active
                              ? context.palette.success
                              : context.colors.primary,
                        ),
                        const SizedBox(width: AppSpacing.xs),
                        StatusPill(
                          label:
                              '${s.addedOn} ${dateFormat.format(word.addedAt.toLocal())}',
                          color:
                              context.colors.onSurface.withValues(alpha: 0.55),
                        ),
                        if (word.state == WordState.active) ...[
                          const SizedBox(width: AppSpacing.xs),
                          StatusPill(
                            label: '${s.exposure} ${word.exposureCount}',
                            color: context.palette.success,
                            icon: Icons.bolt_rounded,
                          ),
                        ],
                      ],
                    ),
                  ],
                ),
              ),
              const SizedBox(height: AppSpacing.lg),
              SectionHeader(title: s.skillsHub),
              for (final state in word.skills) ...[
                _SkillRow(state: state, dateFormat: dateFormat),
                const SizedBox(height: AppSpacing.xs),
              ],
              if (data.events.isNotEmpty) ...[
                const SizedBox(height: AppSpacing.lg),
                SectionHeader(title: s.wordJourney),
                AppCard(
                  child: Column(
                    children: [
                      for (final event in data.events)
                        _EventRow(event: event, dateFormat: dateFormat),
                    ],
                  ),
                ),
              ],
            ],
          );
        },
      ),
    );
  }
}

class _SkillRow extends ConsumerWidget {
  const _SkillRow({required this.state, required this.dateFormat});

  final WordSkillState state;
  final DateFormat dateFormat;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    final color = switch (state.status) {
      SkillStatus.passed => context.palette.success,
      SkillStatus.failed => context.palette.danger,
      SkillStatus.available => SkillVisuals.color(context, state.skill),
      SkillStatus.pending => context.colors.onSurface.withValues(alpha: 0.4),
    };

    return AppCard(
      padding: const EdgeInsets.symmetric(
        horizontal: AppSpacing.md,
        vertical: AppSpacing.sm,
      ),
      child: Row(
        children: [
          Icon(SkillVisuals.icon(state.skill), size: 20, color: color),
          const SizedBox(width: AppSpacing.sm),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(s.skillName(state.skill), style: context.text.titleSmall),
                if (state.availableAt != null &&
                    state.status != SkillStatus.passed)
                  Text(
                    dateFormat.format(state.availableAt!.toLocal()),
                    style: context.text.bodySmall?.copyWith(
                      color: context.colors.onSurface.withValues(alpha: 0.55),
                    ),
                  ),
              ],
            ),
          ),
          StatusPill(label: s.statusLabel(state.status), color: color),
        ],
      ),
    );
  }
}

class _EventRow extends ConsumerWidget {
  const _EventRow({required this.event, required this.dateFormat});

  final WordEvent event;
  final DateFormat dateFormat;

  String _label(AppStrings s) {
    final skill = event.skill == null ? '' : ' · ${s.skillName(event.skill!)}';
    return switch (event.type) {
      WordEventType.added => '${s.addedOn}$skill',
      WordEventType.skillStarted => '${s.openSkill}$skill',
      WordEventType.skillPassed => '${s.statusLabel(SkillStatus.passed)}$skill',
      WordEventType.skillFailed => '${s.statusLabel(SkillStatus.failed)}$skill',
      WordEventType.becameMature => s.stateLabel(WordState.mature),
      WordEventType.enteredActive => s.stateLabel(WordState.active),
      WordEventType.exposureIncremented => s.exposure,
      WordEventType.archived => s.stateLabel(WordState.archived),
    };
  }

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 6),
      child: Row(
        children: [
          Container(
            width: 8,
            height: 8,
            decoration: BoxDecoration(
              color: context.colors.primary.withValues(alpha: 0.6),
              shape: BoxShape.circle,
            ),
          ),
          const SizedBox(width: AppSpacing.sm),
          Expanded(child: Text(_label(s), style: context.text.bodyMedium)),
          Text(
            dateFormat.format(event.createdAt.toLocal()),
            style: context.text.labelSmall?.copyWith(
              color: context.colors.onSurface.withValues(alpha: 0.5),
            ),
          ),
        ],
      ),
    );
  }
}
