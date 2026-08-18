import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_providers.dart';
import '../../core/api/wordos_api.dart';
import '../../core/l10n/app_strings.dart';
import '../../core/models/models.dart';
import '../../core/audio/speech_provider.dart';
import '../../core/theme/app_tokens.dart';
import '../../core/widgets/speaker_button.dart';
import 'spoken_answer_field.dart';
import '../../core/theme/skill_visuals.dart';
import '../../core/widgets/app_widgets.dart';
import '../auth/session_controller.dart';

/// The placement test is **adaptive**: the next question depends on how the
/// previous one was answered, so this screen asks the server for one item at a
/// time instead of receiving a fixed list. The scoring, item choice and level
/// assignment all happen server-side (rule R1, ADR-007); this screen only
/// renders what it is given and posts back what the learner did.
class PlacementScreen extends ConsumerStatefulWidget {
  const PlacementScreen({super.key});

  @override
  ConsumerState<PlacementScreen> createState() => _PlacementScreenState();
}

enum _Phase { intro, question, submitting, result, error }

class _PlacementScreenState extends ConsumerState<PlacementScreen> {
  final TextEditingController _freeText = TextEditingController();

  _Phase _phase = _Phase.intro;
  PlacementStep? _step;
  PlacementResult? _result;
  String _answer = '';
  String? _errorMessage;

  /// Guards the "answer" button while a request is in flight, so a double tap
  /// cannot submit the same item twice.
  bool _busy = false;

  @override
  void dispose() {
    _freeText.dispose();
    super.dispose();
  }

  Future<void> _run(Future<void> Function() body) async {
    if (_busy) return;
    setState(() => _busy = true);
    try {
      await body();
    } on ApiException catch (e) {
      if (mounted) {
        setState(() {
          _phase = _Phase.error;
          _errorMessage = ref.read(stringsProvider).apiError(e.code, e.message);
        });
      }
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _start() => _run(() async {
        setState(() {
          _phase = _Phase.submitting;
          _errorMessage = null;
        });
        final step = await ref.read(wordOsApiProvider).startPlacement();
        if (!mounted) return;
        setState(() {
          _step = step;
          _answer = '';
          _freeText.clear();
          _phase = _Phase.question;
        });
      });

  Future<void> _submitAnswer() => _run(() async {
        final step = _step;
        final item = step?.item;
        if (step == null || item == null) return;

        final next = await ref.read(wordOsApiProvider).answerPlacement(
              sessionId: step.sessionId,
              itemId: item.id,
              answer: _answer,
            );
        if (!mounted) return;

        if (next.isComplete) {
          setState(() => _phase = _Phase.submitting);
          final result =
              await ref.read(wordOsApiProvider).completePlacement(step.sessionId);
          if (!mounted) return;
          setState(() {
            _result = result;
            _phase = _Phase.result;
          });
          return;
        }

        setState(() {
          _step = next;
          _answer = '';
          _freeText.clear();
          _phase = _Phase.question;
        });
      });

  @override
  Widget build(BuildContext context) {
    final s = ref.watch(stringsProvider);

    return Scaffold(
      body: SafeArea(
        child: switch (_phase) {
          _Phase.intro => _IntroView(onStart: _start),
          _Phase.submitting => BusyView(message: s.evaluating),
          _Phase.result => _ResultView(result: _result!),
          _Phase.error => ErrorView(
              message: _errorMessage ?? s.somethingWentWrong,
              retryLabel: s.retry,
              // Restarting is the only safe recovery: the server-side run may
              // have expired, and resuming a half-scored adaptive test from the
              // client would corrupt the estimate.
              onRetry: _start,
            ),
          _Phase.question => _QuestionView(
              step: _step!,
              answer: _answer,
              freeText: _freeText,
              busy: _busy,
              onAnswer: (value) => setState(() => _answer = value),
              onNext: _submitAnswer,
            ),
        },
      ),
    );
  }
}

class _IntroView extends ConsumerWidget {
  const _IntroView({required this.onStart});

  final VoidCallback onStart;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    return Padding(
      padding: const EdgeInsets.all(AppSpacing.lg),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          const Spacer(),
          Icon(Icons.explore_outlined, size: 56, color: context.colors.primary),
          const SizedBox(height: AppSpacing.lg),
          Text(s.placementTitle, style: context.text.headlineMedium),
          const SizedBox(height: AppSpacing.xs),
          Text(
            s.placementSubtitle,
            style: context.text.bodyMedium?.copyWith(
              color: context.colors.onSurface.withValues(alpha: 0.65),
            ),
          ),
          const SizedBox(height: AppSpacing.md),
          AppCard(
            color: context.palette.subtleSurface,
            child: Row(
              children: [
                Icon(Icons.tune_rounded,
                    size: 20, color: context.colors.primary),
                const SizedBox(width: AppSpacing.sm),
                Expanded(
                  child: Text(
                    s.placementAdaptiveNote,
                    style: context.text.bodySmall,
                  ),
                ),
              ],
            ),
          ),
          const Spacer(),
          FilledButton(onPressed: onStart, child: Text(s.start)),
        ],
      ),
    );
  }
}

class _QuestionView extends ConsumerWidget {
  const _QuestionView({
    required this.step,
    required this.answer,
    required this.freeText,
    required this.busy,
    required this.onAnswer,
    required this.onNext,
  });

  final PlacementStep step;
  final String answer;
  final TextEditingController freeText;
  final bool busy;
  final ValueChanged<String> onAnswer;
  final VoidCallback onNext;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    final item = step.item!;
    final progress = step.progress;
    final answered = answer.trim().isNotEmpty;

    return Column(
      children: [
        Padding(
          padding: const EdgeInsets.all(AppSpacing.md),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  StatusPill(
                    label: s.skillName(item.skill),
                    color: SkillVisuals.color(context, item.skill),
                    icon: SkillVisuals.icon(item.skill),
                  ),
                  const Spacer(),
                  // An adaptive test has no fixed length, so this is shown as
                  // an approximation rather than a countdown.
                  Text(
                    s.placementApproxProgress(
                      progress.answered + 1,
                      progress.estimatedTotal,
                    ),
                    style: context.text.labelMedium?.copyWith(
                      color: context.colors.onSurface.withValues(alpha: 0.6),
                    ),
                  ),
                ],
              ),
              const SizedBox(height: AppSpacing.sm),
              StepProgressBar(value: progress.ratio),
            ],
          ),
        ),
        Expanded(
          child: SingleChildScrollView(
            padding: AppSpacing.page,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                if (item.passage != null) ...[
                  AppCard(
                    color: context.palette.subtleSurface,
                    child: Text(item.passage!, style: context.text.bodyLarge),
                  ),
                  const SizedBox(height: AppSpacing.md),
                ],
                if (item.audioText != null) ...[
                  _AudioPlayer(text: item.audioText!),
                  const SizedBox(height: AppSpacing.md),
                ],
                Text(item.prompt, style: context.text.titleMedium),
                const SizedBox(height: AppSpacing.md),
                if (item.type == PlacementItemType.multipleChoice)
                  for (final option in item.options)
                    OptionTile(
                      label: option,
                      selected: answer == option,
                      enabled: !busy,
                      onTap: () => onAnswer(option),
                    )
                // Speaking is spoken (§17). A text box here would measure
                // writing and record the result as speech.
                else if (item.type == PlacementItemType.spoken)
                  SpokenAnswerField(
                    transcript: answer,
                    enabled: !busy,
                    onTranscript: onAnswer,
                  )
                else
                  TextField(
                    controller: freeText,
                    maxLines: 4,
                    enabled: !busy,
                    decoration: InputDecoration(hintText: s.writeYourAnswer),
                    onChanged: onAnswer,
                  ),
                const SizedBox(height: AppSpacing.xl),
              ],
            ),
          ),
        ),
        Padding(
          padding: const EdgeInsets.all(AppSpacing.md),
          child: FilledButton(
            onPressed: answered && !busy ? onNext : null,
            child: busy
                ? const SizedBox(
                    width: 20,
                    height: 20,
                    child: CircularProgressIndicator(
                      strokeWidth: 2.4,
                      color: Colors.white,
                    ),
                  )
                : Text(s.next),
          ),
        ),
      ],
    );
  }
}

class _AudioPlayer extends ConsumerWidget {
  const _AudioPlayer({required this.text});

  final String text;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);

    // The shared control: one tap plays, the next stops, and the icon follows
    // the real playback state rather than a local flag (§10).
    return AppCard(
      color: context.palette.subtleSurface,
      child: Row(
        children: [
          Expanded(
            child: SpeechPlayButton(
              id: 'placement:$text',
              text: text,
              playLabel: s.playAudio,
              stopLabel: s.stopAudio,
            ),
          ),
          const SizedBox(width: AppSpacing.xs),
          SpeakerButton(
            id: 'placement-slow:$text',
            text: text,
            rate: SpeechRate.slow,
            tooltip: s.slowSpeed,
          ),
        ],
      ),
    );
  }
}

class _ResultView extends ConsumerWidget {
  const _ResultView({required this.result});

  final PlacementResult result;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    return Column(
      children: [
        Expanded(
          child: SingleChildScrollView(
            padding: const EdgeInsets.all(AppSpacing.md),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                const SizedBox(height: AppSpacing.lg),
                Icon(Icons.explore_outlined,
                    size: 48, color: context.colors.primary),
                const SizedBox(height: AppSpacing.md),
                Text(s.placementResultTitle,
                    style: context.text.headlineSmall),
                const SizedBox(height: AppSpacing.xs),
                // Said before the bands, not after: by the time a learner has
                // read "A1" they have already decided what it means about them.
                Text(
                  s.placementEstimateNote,
                  style: context.text.bodyMedium?.copyWith(
                    color: context.colors.onSurface.withValues(alpha: 0.75),
                  ),
                ),
                const SizedBox(height: AppSpacing.sm),
                Text(
                  result.summary,
                  style: context.text.bodySmall?.copyWith(
                    color: context.colors.onSurface.withValues(alpha: 0.6),
                  ),
                ),
                const SizedBox(height: AppSpacing.lg),
                for (final level in result.levels)
                  Padding(
                    padding: const EdgeInsets.only(bottom: AppSpacing.xs),
                    child: _LevelRow(
                      level: level,
                      spelling: result.spelling,
                    ),
                  ),
              ],
            ),
          ),
        ),
        Padding(
          padding: const EdgeInsets.all(AppSpacing.md),
          child: FilledButton(
            onPressed: () => ref.read(sessionProvider.notifier).refresh(),
            child: Text(s.startLearning),
          ),
        ),
      ],
    );
  }
}

class _LevelRow extends ConsumerWidget {
  const _LevelRow({required this.level, required this.spelling});

  final SkillLevel level;
  final SpellingDiagnostic spelling;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    final color = SkillVisuals.color(context, level.skill);
    final provisional = level.carriesCefrLevel && level.confidence < 0.5;

    return AppCard(
      child: Row(
        children: [
          Container(
            width: 38,
            height: 38,
            decoration: BoxDecoration(
              color: color.withValues(alpha: 0.12),
              borderRadius: const BorderRadius.all(AppRadii.sm),
            ),
            child: Icon(SkillVisuals.icon(level.skill), size: 20, color: color),
          ),
          const SizedBox(width: AppSpacing.sm),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(s.skillName(level.skill), style: context.text.titleSmall),
                if (provisional) ...[
                  const SizedBox(height: 2),
                  Text(
                    s.placementProvisional,
                    style: context.text.labelSmall
                        ?.copyWith(color: context.palette.warning),
                  ),
                ],
                // Spelling reports what it actually measured instead of
                // borrowing a CEFR band it does not have (ADR-008).
                if (!level.carriesCefrLevel) ...[
                  const SizedBox(height: 2),
                  Text(
                    s.spellingAccuracyLabel(
                      spelling.correct,
                      spelling.itemsAnswered,
                    ),
                    style: context.text.labelSmall?.copyWith(
                      color: context.colors.onSurface.withValues(alpha: 0.6),
                    ),
                  ),
                ],
              ],
            ),
          ),
          if (level.systemAssessedLevel != null)
            LevelBadge(label: level.systemAssessedLevel!.label, color: color)
          else
            StatusPill(label: s.spellingMeasured, color: color),
        ],
      ),
    );
  }
}
