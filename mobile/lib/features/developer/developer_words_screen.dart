import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:intl/intl.dart' hide TextDirection;

import '../../app/router.dart';
import '../../core/api/api_providers.dart';
import '../../core/api/wordos_api.dart';
import '../../core/l10n/app_strings.dart';
import '../../core/models/models.dart';
import '../../core/theme/app_tokens.dart';
import '../../core/theme/skill_visuals.dart';
import '../../core/widgets/app_widgets.dart';

/// The query behind the Owner's view of one learner's vocabulary.
final adminUserWordsProvider = FutureProvider.autoDispose
    .family<AdminWordPage, ({String userId, WordState? state})>(
  (ref, key) => ref
      .watch(wordOsApiProvider)
      .adminUserWords(key.userId, state: key.state),
);

/// One learner's vocabulary, filtered by pipeline state (Part 3).
///
/// The mirror image of My Words. The learner's own screen deliberately hides
/// Learning / Active / Archived, because those are the machinery rather than
/// their vocabulary (Part 2 §42). This screen exists to look at exactly that
/// machinery: which words are stuck, which completed, which were archived when
/// the level grew past them.
class DeveloperWordsScreen extends ConsumerWidget {
  const DeveloperWordsScreen({
    super.key,
    required this.userId,
    required this.state,
  });

  final String userId;

  /// Null lists every word, whatever state it is in.
  final WordState? state;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    final key = (userId: userId, state: state);
    final words = ref.watch(adminUserWordsProvider(key));

    return Scaffold(
      appBar: AppBar(
        title: Text(state == null ? s.vocabulary : s.stateLabel(state!)),
      ),
      body: words.when(
        loading: () => BusyView(message: s.loading),
        error: (e, _) => ErrorView(
          message: e is ApiException ? e.message : s.somethingWentWrong,
          retryLabel: s.retry,
          onRetry: () => ref.invalidate(adminUserWordsProvider(key)),
        ),
        data: (page) {
          if (page.items.isEmpty) {
            return EmptyState(
              icon: Icons.inbox_rounded,
              title: s.noWordsYet,
            );
          }

          return ListView.separated(
            padding: const EdgeInsets.all(AppSpacing.md),
            itemCount: page.items.length + 1,
            separatorBuilder: (_, _) => const SizedBox(height: AppSpacing.xs),
            itemBuilder: (context, index) {
              if (index == 0) {
                return Padding(
                  padding: const EdgeInsets.only(bottom: AppSpacing.xxs),
                  child: Text(
                    s.wordCount(page.total),
                    style: context.text.labelMedium?.copyWith(
                      color: context.colors.onSurface.withValues(alpha: 0.6),
                    ),
                  ),
                );
              }
              return _WordRow(word: page.items[index - 1]);
            },
          );
        },
      ),
    );
  }
}

class _WordRow extends ConsumerWidget {
  const _WordRow({required this.word});

  final AdminWord word;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);

    return AppCard(
      onTap: () => context.push(Routes.developerWord(word.id)),
      child: Row(
        children: [
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Flexible(
                      child: Text(word.text, style: context.text.titleSmall),
                    ),
                    const SizedBox(width: AppSpacing.xs),
                    LevelBadge(label: word.cefrLevel.label),
                  ],
                ),
                const SizedBox(height: 2),
                Text(
                  word.meaning,
                  textDirection: TextDirection.rtl,
                  style: context.text.bodySmall?.copyWith(
                    color: context.colors.onSurface.withValues(alpha: 0.7),
                  ),
                ),
                const SizedBox(height: AppSpacing.xxs),
                Text(
                  // The two figures that say whether this word is going well:
                  // how far it got, and how many attempts that cost.
                  '${s.devSkillsPassed(word.skillsPassed)} · '
                  '${s.devAttempts(word.attempts)} · '
                  '${s.exposure} ${word.exposureCount}',
                  style: context.text.labelSmall?.copyWith(
                    color: context.colors.onSurface.withValues(alpha: 0.55),
                  ),
                ),
              ],
            ),
          ),
          if (word.currentSkill != null)
            StatusPill(
              label: s.skillName(word.currentSkill!),
              color: SkillVisuals.color(context, word.currentSkill!),
              icon: SkillVisuals.icon(word.currentSkill!),
            )
          else
            StatusPill(
              label: s.stateLabel(word.state),
              color: context.palette.success,
            ),
        ],
      ),
    );
  }
}

final adminWordJourneyProvider =
    FutureProvider.autoDispose.family<AdminWordJourney, String>(
  (ref, wordId) => ref.watch(wordOsApiProvider).adminWordJourney(wordId),
);

/// One word's whole life (Part 3).
///
/// Read from the append-only event log rather than from the word's current
/// row, which is the point: a word that failed Reading twice before passing
/// shows all three events. Its current state remembers only the ending.
class DeveloperWordJourneyScreen extends ConsumerWidget {
  const DeveloperWordJourneyScreen({super.key, required this.wordId});

  final String wordId;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    final journey = ref.watch(adminWordJourneyProvider(wordId));

    return Scaffold(
      appBar: AppBar(
        title: Text(journey.valueOrNull?.word.text ?? s.wordJourney),
      ),
      body: journey.when(
        loading: () => BusyView(message: s.loading),
        error: (e, _) => ErrorView(
          message: e is ApiException ? e.message : s.somethingWentWrong,
          retryLabel: s.retry,
          onRetry: () => ref.invalidate(adminWordJourneyProvider(wordId)),
        ),
        data: (data) => _Journey(journey: data),
      ),
    );
  }
}

class _Journey extends ConsumerWidget {
  const _Journey({required this.journey});

  final AdminWordJourney journey;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    final format = DateFormat.yMMMd(s.locale.languageCode).add_Hm();
    final word = journey.word;

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
                    child: Text(word.text, style: context.text.titleMedium),
                  ),
                  LevelBadge(label: word.cefrLevel.label),
                ],
              ),
              const SizedBox(height: AppSpacing.xxs),
              Text(word.meaning, textDirection: TextDirection.rtl),
              const SizedBox(height: AppSpacing.xs),
              Text(
                '${journey.learnerName} · ${s.stateLabel(word.state)}',
                style: context.text.labelMedium?.copyWith(
                  color: context.colors.onSurface.withValues(alpha: 0.6),
                ),
              ),
            ],
          ),
        ),
        const SizedBox(height: AppSpacing.lg),

        SectionHeader(title: s.pipeline),
        for (final skill in journey.skills) ...[
          AppCard(
            child: Row(
              children: [
                Icon(SkillVisuals.icon(skill.skill),
                    size: 18, color: SkillVisuals.color(context, skill.skill)),
                const SizedBox(width: AppSpacing.xs),
                Expanded(child: Text(s.skillName(skill.skill))),
                Text(
                  s.devAttempts(skill.attempts),
                  style: context.text.labelSmall?.copyWith(
                    color: context.colors.onSurface.withValues(alpha: 0.55),
                  ),
                ),
                const SizedBox(width: AppSpacing.xs),
                StatusPill(
                  label: s.statusLabel(skill.status),
                  color: skill.status == SkillStatus.passed
                      ? context.palette.success
                      : context.colors.onSurface.withValues(alpha: 0.5),
                ),
              ],
            ),
          ),
          const SizedBox(height: AppSpacing.xs),
        ],
        const SizedBox(height: AppSpacing.lg),

        SectionHeader(title: s.wordJourney, subtitle: s.devJourneyHint),
        if (journey.events.isEmpty)
          AppCard(child: Text(s.devNoEvents))
        else
          for (final event in journey.events) ...[
            AppCard(
              color: context.palette.subtleSurface,
              child: Row(
                children: [
                  Expanded(
                    child: Text(
                      event.skill == null
                          ? s.wordEventLabel(event.type)
                          : '${s.wordEventLabel(event.type)} · '
                              '${s.skillName(event.skill!)}',
                      style: context.text.bodyMedium,
                    ),
                  ),
                  Text(
                    format.format(event.createdAt.toLocal()),
                    style: context.text.labelSmall?.copyWith(
                      color: context.colors.onSurface.withValues(alpha: 0.55),
                    ),
                  ),
                ],
              ),
            ),
            const SizedBox(height: AppSpacing.xxs),
          ],

        if (journey.exposures.isNotEmpty) ...[
          const SizedBox(height: AppSpacing.lg),
          SectionHeader(title: s.exposure, subtitle: s.devExposureHint),
          AppCard(
            child: Text(
              journey.exposures
                  .map((e) => format.format(e.toLocal()))
                  .join('\n'),
              style: context.text.bodySmall,
            ),
          ),
        ],
      ],
    );
  }
}
