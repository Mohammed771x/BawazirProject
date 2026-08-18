import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/l10n/app_strings.dart';
import '../../core/models/models.dart';
import '../../core/theme/app_tokens.dart';
import '../../core/widgets/speaker_button.dart';
import '../../core/theme/skill_visuals.dart';
import '../../core/widgets/app_widgets.dart';

/// The five-skill progress strip — the clearest expression of "a word is an
/// entity with five independent skill states".
class SkillPips extends StatelessWidget {
  const SkillPips({super.key, required this.skills, this.compact = true});

  final List<WordSkillState> skills;
  final bool compact;

  Color _colorFor(BuildContext context, WordSkillState state) {
    final palette = context.palette;
    return switch (state.status) {
      SkillStatus.passed => palette.success,
      SkillStatus.failed => palette.danger,
      SkillStatus.available => SkillVisuals.color(context, state.skill),
      SkillStatus.pending => palette.border,
    };
  }

  @override
  Widget build(BuildContext context) {
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        for (final state in skills)
          Padding(
            padding: const EdgeInsetsDirectional.only(end: 6),
            child: Container(
              width: compact ? 22 : 30,
              height: compact ? 22 : 30,
              decoration: BoxDecoration(
                color: _colorFor(context, state).withValues(
                  alpha: state.status == SkillStatus.pending ? 0.35 : 0.16,
                ),
                borderRadius: BorderRadius.circular(compact ? 7 : 9),
              ),
              child: Icon(
                SkillVisuals.icon(state.skill),
                size: compact ? 12 : 16,
                color: state.status == SkillStatus.pending
                    ? context.colors.onSurface.withValues(alpha: 0.35)
                    : _colorFor(context, state),
              ),
            ),
          ),
      ],
    );
  }
}

class WordTile extends ConsumerWidget {
  const WordTile({super.key, required this.word, this.onTap});

  final Word word;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    return AppCard(
      onTap: onTap,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Row(
                      children: [
                        Flexible(
                          child: Text(word.text,
                              style: context.text.titleMedium),
                        ),
                        // Every vocabulary item can be heard (§13, §44).
                        SpeakerButton(
                          id: 'word:${word.id}',
                          text: word.text,
                          size: 18,
                        ),
                      ],
                    ),
                    const SizedBox(height: 2),
                    Text(
                      word.meaning,
                      textDirection: TextDirection.rtl,
                      style: context.text.bodyMedium?.copyWith(
                        color: context.colors.onSurface.withValues(alpha: 0.7),
                      ),
                    ),
                  ],
                ),
              ),
              LevelBadge(label: word.cefrLevel.label),
            ],
          ),
          const SizedBox(height: AppSpacing.sm),
          Row(
            children: [
              SkillPips(skills: word.skills),
              const Spacer(),
              // What the learner is told about a word is where it is in their
              // own learning, not which state row the server holds (§42–§45):
              // a word still in the pipeline shows the skill it is waiting on,
              // and everything past it — Active or Archived — reads simply as
              // known. Archived is not a demotion and must never look like one.
              if (word.state == WordState.learning && word.currentSkill != null)
                StatusPill(
                  label: s.skillName(word.currentSkill!),
                  color: SkillVisuals.color(context, word.currentSkill!),
                  icon: SkillVisuals.icon(word.currentSkill!),
                )
              else if (word.state == WordState.active ||
                  word.state == WordState.archived)
                StatusPill(
                  label: s.wordKnown,
                  color: context.palette.success,
                  icon: Icons.check_circle_outline_rounded,
                )
              else
                StatusPill(
                  label: s.stateLabel(word.state),
                  color: context.colors.onSurface.withValues(alpha: 0.5),
                ),
            ],
          ),
        ],
      ),
    );
  }
}
