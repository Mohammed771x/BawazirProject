import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:intl/intl.dart';

import '../../app/router.dart';
import '../../core/api/api_providers.dart';
import '../../core/api/server_revision.dart';
import '../../core/l10n/app_strings.dart';
import '../../core/models/models.dart';
import '../../core/theme/app_tokens.dart';
import '../../core/theme/skill_visuals.dart';
import '../../core/widgets/app_widgets.dart';
import '../auth/session_controller.dart';

final hubProvider = FutureProvider.autoDispose<HubState>((ref) {
  refetchWhenServerChanges(ref);
  return ref.watch(wordOsApiProvider).hub();
});

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
            error: (e, _) => ErrorView.from(
              e,
              s,
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
                // Shown while it is still *coming*, not only once it is
                // open. A learner's first week has no challenge in it
                // (ADR-089), and a card that simply is not there teaches them
                // the feature does not exist — so it waits in plain sight with
                // the date on it.
                if (data.weeklyReview.available ||
                    data.weeklyReview.practiceAvailable ||
                    data.weeklyReview.nextAvailableAt != null) ...[
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
              // The count of words added today, not a quota (§1). "10 / 0"
              // read as a target the learner had already overshot, which is
              // both wrong and discouraging; the daily target is a session-size
              // cap the backend applies, never something to score them against.
              Text(
                '${progress.wordsAddedToday}',
                style: context.text.headlineSmall?.copyWith(
                  color: context.colors.primary,
                ),
              ),
            ],
          ),
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

    // Reading and Listening stay open even with nothing due: the session screen
    // offers a practice passage instead of a closed door (Part 2 §5). The other
    // three are about the words themselves, so with no words there is nothing
    // on the far side of the tap.
    final canPractise =
        card.skill == SkillType.reading || card.skill == SkillType.listening;

    return AppCard(
      onTap: isReady || canPractise
          ? () => context.push(Routes.session(card.skill))
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
    final open = status.available;
    // Finished for the week: the card stays usable, as practice over the
    // same words, instead of locking until next week (ADR-120).
    final practice = !open && status.practiceAvailable;
    final tappable = open || practice;
    String dateOf(DateTime at) =>
        DateFormat.MMMd(s.locale.languageCode).format(at.toLocal());

    // Waiting, not broken. A muted card that names the day is the difference
    // between "this is coming" and "this does not work".
    final accent =
        tappable ? color : context.colors.onSurface.withValues(alpha: 0.45);

    return AppCard(
      borderColor: accent.withValues(alpha: tappable ? 0.35 : 0.2),
      color: accent.withValues(alpha: tappable ? 0.06 : 0.03),
      onTap: open
          ? () => context.push(Routes.weeklyReview)
          : practice
          ? () => context.push(Routes.weeklyReviewPractice)
          : null,
      child: Row(
        children: [
          Icon(
            open
                ? Icons.replay_circle_filled_rounded
                : practice
                ? Icons.task_alt_rounded
                : Icons.lock_clock_rounded,
            color: accent,
            size: 34,
          ),
          const SizedBox(width: AppSpacing.sm),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(s.weeklyReview, style: context.text.titleSmall),
                const SizedBox(height: 2),
                Text(
                  open
                      ? s.wordsDue(status.wordCount)
                      : practice
                      ? s.practiseWeekWords(status.practiceWordCount)
                      : s.challengeOpensOn(dateOf(status.nextAvailableAt!)),
                  style: context.text.bodySmall?.copyWith(color: accent),
                ),
                // The real challenge still has its day, and practising does
                // not move it.
                if (practice && status.nextAvailableAt != null) ...[
                  const SizedBox(height: 2),
                  Text(
                    s.nextChallengeOn(dateOf(status.nextAvailableAt!)),
                    style: context.text.labelSmall?.copyWith(
                      color: context.colors.onSurface.withValues(alpha: 0.6),
                    ),
                  ),
                ],
                // Said before they start, not discovered after they finish
                // what they thought was everything (ADR-089).
                if (open && status.wordsWaitingAfterThis > 0) ...[
                  const SizedBox(height: 2),
                  Text(
                    s.challengeMoreAfterThis(status.wordsWaitingAfterThis),
                    style: context.text.labelSmall?.copyWith(
                      color: context.colors.onSurface.withValues(alpha: 0.6),
                    ),
                  ),
                ],
              ],
            ),
          ),
          if (tappable)
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
