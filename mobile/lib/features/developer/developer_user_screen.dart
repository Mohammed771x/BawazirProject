import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:intl/intl.dart';

import '../../app/router.dart';
import '../../core/api/api_providers.dart';
import '../../core/api/wordos_api.dart';
import '../../core/l10n/app_strings.dart';
import '../../core/models/models.dart';
import '../../core/theme/app_tokens.dart';
import '../../core/theme/skill_visuals.dart';
import '../../core/widgets/app_widgets.dart';
import 'developer_widgets.dart';

final adminUserDetailProvider = FutureProvider.autoDispose
    .family<AdminUserDetail, String>(
      (ref, userId) => ref.watch(wordOsApiProvider).adminUserDetail(userId),
    );

/// One learner's complete journey (`MVP Core.txt` §58–59, Core Components §23).
class DeveloperUserScreen extends ConsumerWidget {
  const DeveloperUserScreen({super.key, required this.userId});

  final String userId;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    final detail = ref.watch(adminUserDetailProvider(userId));

    return Scaffold(
      appBar: AppBar(
        title: Text(detail.valueOrNull?.summary.displayName ?? ''),
      ),
      body: detail.when(
        loading: () => BusyView(message: s.loading),
        error: (e, _) => ErrorView(
          message: e is ApiException ? e.message : s.somethingWentWrong,
          retryLabel: s.retry,
          onRetry: () => ref.invalidate(adminUserDetailProvider(userId)),
        ),
        data: (data) => _Body(detail: data),
      ),
    );
  }
}

class _Body extends ConsumerWidget {
  const _Body({required this.detail});

  final AdminUserDetail detail;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    final dates = DateFormat.yMMMd(s.locale.languageCode);

    return ListView(
      padding: const EdgeInsets.all(AppSpacing.md),
      children: [
        // ── Account ─────────────────────────────────────────────────────────
        SectionHeader(title: s.devAccount),
        AppCard(
          child: Column(
            children: [
              _Row(label: s.email, value: detail.summary.email),
              _Row(
                label: s.devJoined,
                value: dates.format(detail.summary.createdAt.toLocal()),
              ),
              _Row(
                label: s.devLastActiveLabel,
                value: detail.summary.lastActiveAt == null
                    ? s.devNeverActive
                    : dates.format(detail.summary.lastActiveAt!.toLocal()),
              ),
              _Row(label: s.devSignIns, value: '${detail.signInCount}'),
            ],
          ),
        ),
        const SizedBox(height: AppSpacing.lg),

        // ── Interests ───────────────────────────────────────────────────────
        SectionHeader(title: s.interests),
        AppCard(
          child: detail.interests.isEmpty
              ? Text('—', style: context.text.bodySmall)
              : Wrap(
                  spacing: AppSpacing.xs,
                  runSpacing: AppSpacing.xs,
                  children: [
                    for (final interest in detail.interests)
                      StatusPill(
                        label: interest,
                        color: context.colors.primary,
                      ),
                  ],
                ),
        ),
        const SizedBox(height: AppSpacing.lg),

        // ── Vocabulary ──────────────────────────────────────────────────────
        SectionHeader(title: s.vocabulary, subtitle: s.devVocabularyHint),
        GridView.count(
          crossAxisCount: 3,
          shrinkWrap: true,
          physics: const NeverScrollableScrollPhysics(),
          childAspectRatio: 1.35,
          crossAxisSpacing: AppSpacing.xs,
          mainAxisSpacing: AppSpacing.xs,
          children: [
            // Each count opens the words behind it (Part 3): a figure the Owner
            // cannot drill into is a number they have to take on trust.
            MetricTile(
              label: s.learning,
              value: '${detail.wordsLearning}',
              onTap: () => context.push(
                Routes.developerUserWords(
                  detail.summary.id,
                  state: WordState.learning.wire,
                ),
              ),
            ),
            MetricTile(
              label: s.active,
              value: '${detail.wordsActive}',
              onTap: () => context.push(
                Routes.developerUserWords(
                  detail.summary.id,
                  state: WordState.active.wire,
                ),
              ),
            ),
            MetricTile(
              label: s.archived,
              value: '${detail.wordsArchived}',
              onTap: () => context.push(
                Routes.developerUserWords(
                  detail.summary.id,
                  state: WordState.archived.wire,
                ),
              ),
            ),
            MetricTile(label: s.devToday, value: '${detail.wordsAddedToday}'),
            MetricTile(
              label: s.devThisWeek,
              value: '${detail.wordsAddedThisWeek}',
            ),
            MetricTile(
              label: s.devThisMonth,
              value: '${detail.wordsAddedThisMonth}',
            ),
          ],
        ),
        const SizedBox(height: AppSpacing.lg),

        // ── Levels ──────────────────────────────────────────────────────────
        SectionHeader(title: s.skillLevels, subtitle: s.devLevelsHint),
        // The way in to the evidence: a level is a conclusion, and the Owner
        // should always be one tap from what produced it (Part 3).
        AppCard(
          onTap: () =>
              context.push(Routes.developerPlacement(detail.summary.id)),
          color: context.palette.subtleSurface,
          child: Row(
            children: [
              Icon(Icons.fact_check_outlined,
                  size: 18,
                  color: context.colors.onSurface.withValues(alpha: 0.7)),
              const SizedBox(width: AppSpacing.xs),
              Expanded(child: Text(s.devPlacementEvidence)),
              const Icon(Icons.chevron_right_rounded),
            ],
          ),
        ),
        const SizedBox(height: AppSpacing.xs),
        for (final level in detail.levels) ...[
          AppCard(
            child: Row(
              children: [
                Icon(
                  SkillVisuals.icon(level.skill),
                  size: 20,
                  color: SkillVisuals.color(context, level.skill),
                ),
                const SizedBox(width: AppSpacing.sm),
                Expanded(
                  child: Text(
                    s.skillName(level.skill),
                    style: context.text.titleSmall,
                  ),
                ),
                if (level.carriesCefrLevel) ...[
                  // Both levels are shown side by side, never merged — the
                  // difference between them is the point (rule R6).
                  _LevelChip(
                    caption: s.yourLevel,
                    value: level.userSelectedLevel?.label ?? '—',
                    color: context.colors.onSurface.withValues(alpha: 0.5),
                  ),
                  const SizedBox(width: AppSpacing.xs),
                  _LevelChip(
                    caption: s.systemLevel,
                    value: level.systemAssessedLevel?.label ?? '—',
                    color: SkillVisuals.color(context, level.skill),
                  ),
                ] else
                  _LevelChip(
                    caption: s.spellingMeasured,
                    value: '${(level.rollingAccuracy * 100).round()}%',
                    color: SkillVisuals.color(context, level.skill),
                  ),
              ],
            ),
          ),
          const SizedBox(height: AppSpacing.xs),
        ],
        const SizedBox(height: AppSpacing.lg),

        // ── Per-skill performance ───────────────────────────────────────────
        SectionHeader(
          title: s.devSkillPerformance,
          subtitle: s.devSkillPerformanceHint,
        ),
        AppCard(
          child: Column(
            children: [
              for (final stat in detail.skillStats)
                Padding(
                  padding: const EdgeInsets.only(bottom: AppSpacing.xs),
                  child: Row(
                    children: [
                      SizedBox(
                        width: 80,
                        child: Text(
                          s.skillName(stat.skill),
                          style: context.text.labelSmall,
                          overflow: TextOverflow.ellipsis,
                        ),
                      ),
                      Expanded(
                        child: Text(
                          s.devSkillStatLine(
                            stat.sessionsCompleted,
                            stat.wordsPassed,
                            stat.wordsFailed,
                          ),
                          style: context.text.labelSmall?.copyWith(
                            color: context.colors.onSurface.withValues(
                              alpha: 0.65,
                            ),
                          ),
                        ),
                      ),
                      StatusPill(
                        label: '${(stat.passRate * 100).round()}%',
                        color: stat.wordsAttempted == 0
                            ? context.colors.onSurface.withValues(alpha: 0.4)
                            : stat.passRate >= 0.7
                            ? context.palette.success
                            : context.palette.danger,
                      ),
                    ],
                  ),
                ),
            ],
          ),
        ),
        const SizedBox(height: AppSpacing.lg),

        // ── Day by day ──────────────────────────────────────────────────────
        SectionHeader(title: s.devDaily, subtitle: s.devDailyHint),
        AppCard(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(s.devDailyWordsAdded, style: context.text.labelSmall),
              const SizedBox(height: AppSpacing.xs),
              ColumnChart(
                columns: [
                  for (final row in detail.daily)
                    ChartColumn(
                      label: DateFormat.d().format(row.date),
                      value: row.wordsAdded.toDouble(),
                      color: row.signedIn
                          ? context.colors.primary
                          : context.colors.onSurface.withValues(alpha: 0.2),
                    ),
                ],
              ),
              const SizedBox(height: AppSpacing.sm),
              Text(s.devDailySessions, style: context.text.labelSmall),
              const SizedBox(height: AppSpacing.xs),
              ColumnChart(
                columns: [
                  for (final row in detail.daily)
                    ChartColumn(
                      label: DateFormat.d().format(row.date),
                      value: row.perSkillCompleted.values
                          .fold(0, (a, b) => a + b)
                          .toDouble(),
                      color: context.palette.skillListening,
                    ),
                ],
              ),
            ],
          ),
        ),
        const SizedBox(height: AppSpacing.lg),

        // ── Mistakes ────────────────────────────────────────────────────────
        SectionHeader(title: s.devMistakes, subtitle: s.devMistakesHint),
        AppCard(
          child: detail.mistakes.isEmpty
              ? Text(s.devNoMistakes, style: context.text.bodySmall)
              : Column(
                  children: [
                    for (final mistake in detail.mistakes)
                      // Tappable: "this word keeps failing" is the start of a
                      // question, and the journey behind it is the answer.
                      InkWell(
                        onTap: () =>
                            context.push(Routes.developerWord(mistake.wordId)),
                        child: Padding(
                          padding: const EdgeInsets.only(bottom: AppSpacing.xs),
                          child: Row(
                            children: [
                              Expanded(
                                child: Column(
                                  crossAxisAlignment: CrossAxisAlignment.start,
                                  children: [
                                    Text(
                                      mistake.text,
                                      style: context.text.titleSmall,
                                    ),
                                    Text(
                                      mistake.meaning,
                                      style: context.text.labelSmall?.copyWith(
                                        color: context.colors.onSurface
                                            .withValues(alpha: 0.6),
                                      ),
                                    ),
                                  ],
                                ),
                              ),
                              StatusPill(
                                label: s.skillName(mistake.skill),
                                color: SkillVisuals.color(
                                  context,
                                  mistake.skill,
                                ),
                              ),
                              const SizedBox(width: AppSpacing.xs),
                              StatusPill(
                                label: '×${mistake.attempts}',
                                color: context.palette.danger,
                              ),
                            ],
                          ),
                        ),
                      ),
                  ],
                ),
        ),
        const SizedBox(height: AppSpacing.lg),

        // ── Level history ───────────────────────────────────────────────────
        SectionHeader(
          title: s.devLevelHistory,
          subtitle: s.devLevelHistoryHint,
        ),
        AppCard(
          child: detail.levelChanges.isEmpty
              ? Text(s.devNoLevelChanges, style: context.text.bodySmall)
              : Column(
                  children: [
                    Row(
                      children: [
                        Expanded(
                          child: Text(
                            s.devSystemChanges(detail.systemLevelChanges),
                            style: context.text.labelSmall?.copyWith(
                              color: context.palette.success,
                            ),
                          ),
                        ),
                        Text(
                          s.devManualChanges(detail.manualLevelChanges),
                          style: context.text.labelSmall?.copyWith(
                            color: context.colors.onSurface.withValues(
                              alpha: 0.6,
                            ),
                          ),
                        ),
                      ],
                    ),
                    const SizedBox(height: AppSpacing.xs),
                    for (final change in detail.levelChanges.reversed.take(10))
                      Padding(
                        padding: const EdgeInsets.only(bottom: AppSpacing.xxs),
                        child: Row(
                          children: [
                            SizedBox(
                              width: 80,
                              child: Text(
                                s.skillName(change.skill),
                                style: context.text.labelSmall,
                                overflow: TextOverflow.ellipsis,
                              ),
                            ),
                            Expanded(
                              child: Text(
                                '${change.previous?.label ?? '—'} → '
                                '${change.next?.label ?? '—'}',
                                style: context.text.labelMedium,
                              ),
                            ),
                            // Manual and validated changes are never conflated
                            // — the difference is the signal (rule R6).
                            StatusPill(
                              label:
                                  change.changeType ==
                                      LevelChangeType.systemValidated
                                  ? s.systemLevel
                                  : s.yourLevel,
                              color:
                                  change.changeType ==
                                      LevelChangeType.systemValidated
                                  ? context.palette.success
                                  : context.colors.onSurface.withValues(
                                      alpha: 0.45,
                                    ),
                            ),
                          ],
                        ),
                      ),
                  ],
                ),
        ),
        const SizedBox(height: AppSpacing.lg),

        // ── Mastered ────────────────────────────────────────────────────────
        SectionHeader(title: s.devMastered, subtitle: s.devMasteredHint),
        AppCard(
          child: detail.masteredWords.isEmpty
              ? Text(s.devNoneYet, style: context.text.bodySmall)
              : Wrap(
                  spacing: AppSpacing.xs,
                  runSpacing: AppSpacing.xs,
                  children: [
                    for (final word in detail.masteredWords)
                      StatusPill(label: word, color: context.palette.success),
                  ],
                ),
        ),
        const SizedBox(height: AppSpacing.xxl),
      ],
    );
  }
}

class _Row extends StatelessWidget {
  const _Row({required this.label, required this.value});

  final String label;
  final String value;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 3),
      child: Row(
        children: [
          Expanded(
            child: Text(
              label,
              style: context.text.labelSmall?.copyWith(
                color: context.colors.onSurface.withValues(alpha: 0.6),
              ),
            ),
          ),
          Text(value, style: context.text.labelMedium),
        ],
      ),
    );
  }
}

class _LevelChip extends StatelessWidget {
  const _LevelChip({
    required this.caption,
    required this.value,
    required this.color,
  });

  final String caption;
  final String value;
  final Color color;

  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.end,
      children: [
        Text(
          caption,
          style: context.text.labelSmall?.copyWith(
            fontSize: 9,
            color: context.colors.onSurface.withValues(alpha: 0.5),
          ),
        ),
        Text(value, style: context.text.titleSmall?.copyWith(color: color)),
      ],
    );
  }
}
