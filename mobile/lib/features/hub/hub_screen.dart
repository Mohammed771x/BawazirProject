import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:intl/intl.dart';

import '../../app/router.dart';
import '../../core/api/api_providers.dart';
import '../../core/l10n/app_strings.dart';
import '../../core/models/models.dart';
import '../../core/theme/app_tokens.dart';
import '../../core/theme/skill_visuals.dart';
import '../../core/widgets/app_widgets.dart';
import '../auth/session_controller.dart';

final hubProvider = FutureProvider.autoDispose<HubState>(
  (ref) => ref.watch(wordOsApiProvider).hub(),
);

/// The Skills Hub — the user's control point. It shows *what the backend says*
/// is available; the number of words behind each skill stays deliberately calm
/// (User Flow §17: never dump "you have 50 words" on the learner).
class HubScreen extends ConsumerWidget {
  const HubScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    final hub = ref.watch(hubProvider);
    final user = ref.watch(sessionProvider).user;

    return Scaffold(
      body: SafeArea(
        bottom: false,
        child: RefreshIndicator(
          onRefresh: () async => ref.refresh(hubProvider.future),
          child: hub.when(
            loading: () => BusyView(message: s.loading),
            error: (e, _) => ErrorView(
              message: s.somethingWentWrong,
              retryLabel: s.retry,
              onRetry: () => ref.invalidate(hubProvider),
            ),
            data: (data) => ListView(
              padding: const EdgeInsets.fromLTRB(
                AppSpacing.md,
                AppSpacing.md,
                AppSpacing.md,
                // Clear the floating "Add word" button and the nav bar.
                AppSpacing.xxl * 2,
              ),
              children: [
                _Greeting(name: user?.displayName ?? ''),
                const SizedBox(height: AppSpacing.md),
                _DailyProgressCard(progress: data.dailyProgress),
                const SizedBox(height: AppSpacing.lg),
                SectionHeader(title: s.skillsHub),
                for (final card in data.skills) ...[
                  _SkillCardTile(card: card),
                  const SizedBox(height: AppSpacing.xs),
                ],
                if (data.weeklyReview.available) ...[
                  const SizedBox(height: AppSpacing.md),
                  _WeeklyReviewCard(status: data.weeklyReview),
                ],
                const SizedBox(height: AppSpacing.lg),
                _VocabularySummary(counts: data.vocabulary),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

class _Greeting extends ConsumerWidget {
  const _Greeting({required this.name});

  final String name;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          name.isEmpty ? s.appName : name,
          style: context.text.headlineSmall,
        ),
        const SizedBox(height: 2),
        Text(
          s.tagline,
          style: context.text.bodySmall?.copyWith(
            color: context.colors.onSurface.withValues(alpha: 0.6),
          ),
        ),
      ],
    );
  }
}

class _DailyProgressCard extends ConsumerWidget {
  const _DailyProgressCard({required this.progress});

  final DailyProgress progress;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    return AppCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Expanded(
                child: Text(s.todayProgress, style: context.text.titleSmall),
              ),
              Text(
                '${progress.wordsAddedToday} / ${progress.dailyTarget}',
                style: context.text.titleMedium?.copyWith(
                  color: context.colors.primary,
                ),
              ),
            ],
          ),
          const SizedBox(height: AppSpacing.sm),
          StepProgressBar(value: progress.ratio),
        ],
      ),
    );
  }
}

class _SkillCardTile extends ConsumerWidget {
  const _SkillCardTile({required this.card});

  final SkillCard card;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    final color = SkillVisuals.color(context, card.skill);
    final isReady = card.availability == SkillAvailability.available;

    return AppCard(
      onTap: isReady
          ? () async {
              await context.push(Routes.session(card.skill));
              ref.invalidate(hubProvider);
            }
          : null,
      borderColor: isReady ? color.withValues(alpha: 0.35) : null,
      child: Row(
        children: [
          Container(
            width: 46,
            height: 46,
            decoration: BoxDecoration(
              color: color.withValues(alpha: isReady ? 0.14 : 0.07),
              borderRadius: const BorderRadius.all(AppRadii.md),
            ),
            child: Icon(
              SkillVisuals.icon(card.skill),
              color: color.withValues(alpha: isReady ? 1 : 0.5),
            ),
          ),
          const SizedBox(width: AppSpacing.sm),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Text(
                      s.skillName(card.skill),
                      style: context.text.titleSmall,
                    ),
                    const SizedBox(width: AppSpacing.xs),
                    // Spelling carries no CEFR band, so it shows no badge
                    // rather than a fake one (ADR-008).
                    if (card.level != null)
                      LevelBadge(label: card.level!.label, color: color),
                  ],
                ),
                const SizedBox(height: 2),
                Text(
                  isReady
                      ? s.wordsDue(card.sessionWordCount)
                      : card.nextDueAt != null
                      ? s.nextDue(
                          DateFormat.MMMd(
                            s.locale.languageCode,
                          ).format(card.nextDueAt!.toLocal()),
                        )
                      : s.nothingDue,
                  style: context.text.bodySmall?.copyWith(
                    color: isReady
                        ? color
                        : context.colors.onSurface.withValues(alpha: 0.55),
                    fontWeight: isReady ? FontWeight.w600 : null,
                  ),
                ),
              ],
            ),
          ),
          Icon(
            Icons.chevron_right_rounded,
            color: context.colors.onSurface.withValues(
              alpha: isReady ? 0.65 : 0.25,
            ),
          ),
        ],
      ),
    );
  }
}

class _WeeklyReviewCard extends ConsumerWidget {
  const _WeeklyReviewCard({required this.status});

  final WeeklyReviewStatus status;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    final color = context.palette.review;

    return AppCard(
      borderColor: color.withValues(alpha: 0.35),
      color: color.withValues(alpha: 0.06),
      onTap: () async {
        await context.push(Routes.weeklyReview);
        ref.invalidate(hubProvider);
      },
      child: Row(
        children: [
          Icon(Icons.replay_circle_filled_rounded, color: color, size: 34),
          const SizedBox(width: AppSpacing.sm),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(s.weeklyReview, style: context.text.titleSmall),
                const SizedBox(height: 2),
                Text(
                  s.wordsDue(status.wordCount),
                  style: context.text.bodySmall?.copyWith(color: color),
                ),
              ],
            ),
          ),
          Icon(
            Icons.chevron_right_rounded,
            color: context.colors.onSurface.withValues(alpha: 0.65),
          ),
        ],
      ),
    );
  }
}

class _VocabularySummary extends ConsumerWidget {
  const _VocabularySummary({required this.counts});

  final VocabularyCounts counts;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    return Row(
      children: [
        Expanded(
          child: _CountTile(
            label: s.learning,
            value: counts.learning,
            color: context.colors.primary,
          ),
        ),
        const SizedBox(width: AppSpacing.xs),
        Expanded(
          child: _CountTile(
            label: s.active,
            value: counts.active,
            color: context.palette.success,
          ),
        ),
        const SizedBox(width: AppSpacing.xs),
        Expanded(
          child: _CountTile(
            label: s.archived,
            value: counts.archived,
            color: context.colors.onSurface.withValues(alpha: 0.5),
          ),
        ),
      ],
    );
  }
}

class _CountTile extends StatelessWidget {
  const _CountTile({
    required this.label,
    required this.value,
    required this.color,
  });

  final String label;
  final int value;
  final Color color;

  @override
  Widget build(BuildContext context) {
    return AppCard(
      padding: const EdgeInsets.symmetric(
        vertical: AppSpacing.sm,
        horizontal: AppSpacing.xs,
      ),
      child: Column(
        children: [
          Text(
            '$value',
            style: context.text.headlineSmall?.copyWith(color: color),
          ),
          const SizedBox(height: 2),
          Text(
            label,
            textAlign: TextAlign.center,
            style: context.text.labelSmall?.copyWith(
              color: context.colors.onSurface.withValues(alpha: 0.6),
            ),
          ),
        ],
      ),
    );
  }
}
