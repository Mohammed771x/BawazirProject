import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:intl/intl.dart' hide TextDirection;

import '../../core/api/api_providers.dart';
import '../../core/api/wordos_api.dart';
import '../../core/l10n/app_strings.dart';
import '../../core/models/models.dart';
import '../../core/theme/app_tokens.dart';
import '../../core/theme/skill_visuals.dart';
import '../../core/widgets/app_widgets.dart';

final adminPlacementProvider =
    FutureProvider.autoDispose.family<PlacementEvidence, String>(
  (ref, userId) => ref.watch(wordOsApiProvider).adminPlacementEvidence(userId),
);

/// The placement test behind a learner's starting levels (Part 3).
///
/// A CEFR band is a conclusion, and this is the evidence for it: which items
/// were asked, at what level and in which domain, and — for the free-text and
/// spoken ones — what the learner actually said. That last part is why the raw
/// answers are stored at all; a level alone can be believed but never audited.
///
/// The first section answers the other question the Owner has: not "what level
/// are they?" but "have they moved?" — placement's band beside the current one.
class DeveloperPlacementScreen extends ConsumerWidget {
  const DeveloperPlacementScreen({super.key, required this.userId});

  final String userId;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    final evidence = ref.watch(adminPlacementProvider(userId));

    return Scaffold(
      appBar: AppBar(title: Text(s.devPlacementEvidence)),
      body: evidence.when(
        loading: () => BusyView(message: s.loading),
        error: (e, _) => ErrorView(
          message: e is ApiException ? e.message : s.somethingWentWrong,
          retryLabel: s.retry,
          onRetry: () => ref.invalidate(adminPlacementProvider(userId)),
        ),
        data: (data) => _Evidence(evidence: data),
      ),
    );
  }
}

class _Evidence extends ConsumerWidget {
  const _Evidence({required this.evidence});

  final PlacementEvidence evidence;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);

    return ListView(
      padding: const EdgeInsets.all(AppSpacing.md),
      children: [
        // ── Where they started against where they are ───────────────────────
        SectionHeader(
          title: s.devInitialVsCurrent,
          subtitle: s.devInitialVsCurrentHint,
        ),
        for (final row in evidence.progress) ...[
          AppCard(
            child: Row(
              children: [
                Icon(SkillVisuals.icon(row.skill),
                    size: 18, color: SkillVisuals.color(context, row.skill)),
                const SizedBox(width: AppSpacing.xs),
                Expanded(child: Text(s.skillName(row.skill))),
                LevelBadge(label: row.initialLevel?.label ?? '—'),
                Padding(
                  padding: const EdgeInsets.symmetric(
                      horizontal: AppSpacing.xxs),
                  child: Icon(
                    Icons.arrow_forward_rounded,
                    size: 14,
                    color: context.colors.onSurface.withValues(alpha: 0.4),
                  ),
                ),
                LevelBadge(
                  label: row.currentLevel?.label ?? '—',
                  color: row.currentLevel != null &&
                          row.initialLevel != null &&
                          row.currentLevel!.index > row.initialLevel!.index
                      ? context.palette.success
                      : null,
                ),
              ],
            ),
          ),
          const SizedBox(height: AppSpacing.xs),
        ],
        const SizedBox(height: AppSpacing.lg),

        // ── The evidence itself ─────────────────────────────────────────────
        SectionHeader(
          title: s.devPlacementAnswers,
          subtitle: s.devPlacementAnswersHint,
        ),
        AppCard(
          color: context.palette.subtleSurface,
          child: Row(
            children: [
              Expanded(
                child: Text(
                  // The version matters as much as the answers: a result from
                  // an older item bank is not comparable to a current one.
                  s.devTestVersion(evidence.testVersion),
                  style: context.text.labelMedium,
                ),
              ),
              if (evidence.fallbackScoredCount > 0)
                StatusPill(
                  label: s.devFallbackScored(evidence.fallbackScoredCount),
                  color: context.palette.warning,
                ),
            ],
          ),
        ),
        const SizedBox(height: AppSpacing.xs),
        if (evidence.answers.isEmpty)
          AppCard(child: Text(s.devNoPlacement))
        else
          for (final answer in evidence.answers) ...[
            _AnswerCard(answer: answer),
            const SizedBox(height: AppSpacing.xs),
          ],
      ],
    );
  }
}

class _AnswerCard extends ConsumerWidget {
  const _AnswerCard({required this.answer});

  final PlacementEvidenceItem answer;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    // Partial credit, so this is a spectrum rather than right/wrong — a 0.6 on
    // a free-text answer is real information that a tick would throw away.
    final tone = answer.score >= 0.75
        ? context.palette.success
        : answer.score >= 0.4
            ? context.palette.warning
            : context.palette.danger;

    return AppCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              StatusPill(
                label: s.skillName(answer.skill),
                color: SkillVisuals.color(context, answer.skill),
              ),
              const SizedBox(width: AppSpacing.xxs),
              // The domain, which is not always the skill: grammar items are
              // evidence for Speaking and Writing, and spelling is measured
              // without being a visible skill at all.
              if (answer.domain.isNotEmpty &&
                  answer.domain.toUpperCase() != answer.skill.wire)
                StatusPill(
                  label: answer.domain,
                  color: context.colors.onSurface.withValues(alpha: 0.5),
                ),
              const Spacer(),
              LevelBadge(label: answer.level.label),
              const SizedBox(width: AppSpacing.xxs),
              StatusPill(
                label: answer.score.toStringAsFixed(2),
                color: tone,
              ),
            ],
          ),
          if (answer.alsoEvidenceFor != null) ...[
            const SizedBox(height: AppSpacing.xxs),
            Text(
              s.devAlsoEvidenceFor(s.skillName(answer.alsoEvidenceFor!)),
              style: context.text.labelSmall?.copyWith(
                color: context.colors.onSurface.withValues(alpha: 0.55),
              ),
            ),
          ],
          if (answer.rawAnswer != null && answer.rawAnswer!.isNotEmpty) ...[
            const SizedBox(height: AppSpacing.xs),
            Text(
              answer.rawAnswer!,
              style: context.text.bodyMedium?.copyWith(
                fontStyle: FontStyle.italic,
                color: context.colors.onSurface.withValues(alpha: 0.8),
              ),
            ),
          ],
          const SizedBox(height: AppSpacing.xxs),
          Text(
            '${answer.itemId} · '
            '${DateFormat.yMMMd(s.locale.languageCode).format(answer.answeredAt.toLocal())}',
            style: context.text.labelSmall?.copyWith(
              color: context.colors.onSurface.withValues(alpha: 0.45),
            ),
          ),
        ],
      ),
    );
  }
}
