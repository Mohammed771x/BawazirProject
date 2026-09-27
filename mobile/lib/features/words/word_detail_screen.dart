import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:intl/intl.dart' hide TextDirection;

import '../../core/api/api_providers.dart';
import '../../core/api/server_revision.dart';
import '../../core/l10n/app_strings.dart';
import '../../core/models/models.dart';
import '../../core/theme/app_tokens.dart';
import '../../core/widgets/speaker_button.dart';
import '../../core/theme/skill_visuals.dart';
import '../../core/api/wordos_api.dart';
import '../../core/widgets/app_widgets.dart';

final wordDetailProvider = FutureProvider.autoDispose
    .family<WordDetail, String>((ref, id) {
      refetchWhenServerChanges(ref);
      return ref.watch(wordOsApiProvider).wordDetail(id);
    });

/// The word's full journey: five skill states with their schedules, plus the
/// event history that the MVP needs for algorithm validation.
///
/// Also where a word is removed (ADR-071). Deliberately here rather than as a
/// swipe on the list: this is the one screen that shows what deleting actually
/// costs — four passed skills, or none — and a learner who can see that is
/// making a decision rather than a gesture.
class WordDetailScreen extends ConsumerStatefulWidget {
  const WordDetailScreen({super.key, required this.wordId});

  final String wordId;

  @override
  ConsumerState<WordDetailScreen> createState() => _WordDetailScreenState();
}

class _WordDetailScreenState extends ConsumerState<WordDetailScreen> {
  String get wordId => widget.wordId;

  bool _deleting = false;
  bool _savingMeaning = false;

  /// The meaning field, owned by this screen rather than by the dialog.
  ///
  /// A controller created for a dialog and disposed when it returns is disposed
  /// while the dialog is still animating away, and the next frame reads a dead
  /// controller and takes the app down — the same crash ADR-036 fixed in the
  /// dashboard's custom-range dialog. Living here, it is disposed once, with
  /// the screen.
  final TextEditingController _meaningField = TextEditingController();

  @override
  void dispose() {
    _meaningField.dispose();
    super.dispose();
  }

  /// Rewrites the Arabic meaning of this word (ADR-101).
  ///
  /// Three answers are possible and each is a different conversation. Accepted
  /// is a snackbar. A wording the dictionary knows belongs to *another* English
  /// word is an offer to swap, which is not a refusal — the learner was on a
  /// different word and the useful thing is to say which. A wording nothing
  /// recognises is the checker's softer objection, which they may overrule
  /// (ADR-074).
  Future<void> _editMeaning(Word word) async {
    final s = ref.read(stringsProvider);

    final typed = await _askForMeaning(word, initial: word.meaning);
    if (typed == null || !mounted) return;

    await _saveMeaning(word, typed, s);
  }

  Future<void> _saveMeaning(
    Word word,
    String meaning,
    AppStrings s, {
    bool acceptAnyway = false,
  }) async {
    setState(() => _savingMeaning = true);
    try {
      await ref
          .read(wordOsApiProvider)
          .changeWordMeaning(
            wordId: wordId,
            meaning: meaning,
            acceptAnyway: acceptAnyway,
          );

      if (!mounted) return;
      setState(() => _savingMeaning = false);
      ref.invalidate(wordDetailProvider(wordId));
      ScaffoldMessenger.of(
        context,
      ).showSnackBar(SnackBar(content: Text(s.meaningUpdated)));
    } on MeaningIsAnotherWordException catch (e) {
      if (!mounted) return;
      setState(() => _savingMeaning = false);
      await _offerSwap(word, meaning, e, s);
    } on MeaningRejectedException catch (e) {
      if (!mounted) return;
      setState(() => _savingMeaning = false);

      // The checker could not name another owner for it, so this is the
      // objection the learner is allowed to overrule.
      final keep = await showDialog<bool>(
        context: context,
        builder: (dialogContext) => AlertDialog(
          title: Text(s.meaningLooksWrong),
          content: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(e.message, textDirection: TextDirection.rtl),
              if (e.suggestions.isNotEmpty) ...[
                const SizedBox(height: AppSpacing.sm),
                Text(s.meaningSuggestions, style: context.text.labelMedium),
                const SizedBox(height: AppSpacing.xxs),
                Text(
                  e.suggestions.join('، '),
                  textDirection: TextDirection.rtl,
                ),
              ],
            ],
          ),
          actions: [
            TextButton(
              onPressed: () => Navigator.of(dialogContext).pop(false),
              child: Text(s.cancel),
            ),
            FilledButton(
              onPressed: () => Navigator.of(dialogContext).pop(true),
              child: Text(s.keepMyMeaning),
            ),
          ],
        ),
      );

      if (keep == true && mounted) {
        await _saveMeaning(word, meaning, s, acceptAnyway: true);
      }
    } catch (rawError) {
      final e = ApiException.from(rawError);
      if (!mounted) return;
      setState(() => _savingMeaning = false);
      ScaffoldMessenger.of(
        context,
      ).showSnackBar(SnackBar(content: Text(s.apiError(e.code, e.message))));
    }
  }

  /// "That is `book`, not `create` — shall I swap them?"
  ///
  /// Accepting is destructive in a way deleting a word is: this word goes, and
  /// the new one starts at Reading having been tested on nothing. Both halves
  /// are said before the button is offered.
  Future<void> _offerSwap(
    Word word,
    String meaning,
    MeaningIsAnotherWordException rejection,
    AppStrings s,
  ) async {
    final other = rejection.candidates.firstOrNull;
    if (other == null || other.senseId == null) {
      ScaffoldMessenger.of(
        context,
      ).showSnackBar(SnackBar(content: Text(rejection.message)));
      return;
    }

    final swap = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: Text(s.meaningIsAnotherWordTitle),
        content: Text(
          s.meaningIsAnotherWordBody(meaning, other.text, word.text),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(dialogContext).pop(false),
            child: Text(s.cancel),
          ),
          FilledButton(
            onPressed: () => Navigator.of(dialogContext).pop(true),
            child: Text(s.swapForWord(other.text)),
          ),
        ],
      ),
    );

    if (swap != true || !mounted) return;

    setState(() => _savingMeaning = true);
    try {
      await ref
          .read(wordOsApiProvider)
          .replaceWord(
            wordId: wordId,
            meaning: meaning,
            senseId: other.senseId!,
          );

      if (!mounted) return;
      // This word no longer exists, so this screen has nothing to show. The
      // list behind it is where the replacement is.
      ScaffoldMessenger.of(
        context,
      ).showSnackBar(SnackBar(content: Text(s.wordSwapped(other.text))));
      Navigator.of(context).maybePop();
    } catch (rawError) {
      final e = ApiException.from(rawError);
      if (!mounted) return;
      setState(() => _savingMeaning = false);
      ScaffoldMessenger.of(
        context,
      ).showSnackBar(SnackBar(content: Text(s.apiError(e.code, e.message))));
    }
  }

  /// The field itself, pre-filled with what the word means today.
  Future<String?> _askForMeaning(Word word, {required String initial}) {
    final s = ref.read(stringsProvider);
    final controller = _meaningField..text = initial;

    return showDialog<String>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: Text(s.changeMeaningTitle),
        content: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              s.changeMeaningNote(word.text),
              style: context.text.bodySmall?.copyWith(
                color: context.colors.onSurface.withValues(alpha: 0.7),
              ),
            ),
            const SizedBox(height: AppSpacing.sm),
            TextField(
              controller: controller,
              autofocus: true,
              textDirection: TextDirection.rtl,
              textInputAction: TextInputAction.done,
              onSubmitted: (value) =>
                  Navigator.of(dialogContext).pop(value.trim()),
            ),
          ],
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(dialogContext).pop(),
            child: Text(s.cancel),
          ),
          // Rebuilt as they type rather than with a setState per keystroke,
          // for the same reason the session footer is (ADR-095).
          ValueListenableBuilder(
            valueListenable: controller,
            builder: (_, value, _) => FilledButton(
              onPressed: value.text.trim().isEmpty
                  ? null
                  : () => Navigator.of(dialogContext).pop(value.text.trim()),
              child: Text(s.save),
            ),
          ),
        ],
      ),
    );
  }

  /// Asks, then deletes, then leaves.
  ///
  /// The confirmation is not ceremony: five skills and eight days of waiting
  /// can be behind a word, and an accidental tap is not recoverable by anything
  /// the learner can reach — adding it back starts from Reading.
  Future<void> _confirmDelete(Word word) async {
    final s = ref.read(stringsProvider);

    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: Text(s.deleteWordConfirmTitle),
        content: Text(s.deleteWordConfirmBody(word.text)),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(dialogContext).pop(false),
            child: Text(s.cancel),
          ),
          FilledButton(
            style: FilledButton.styleFrom(
              backgroundColor: context.palette.danger,
            ),
            onPressed: () => Navigator.of(dialogContext).pop(true),
            child: Text(s.deleteWord),
          ),
        ],
      ),
    );

    if (confirmed != true || !mounted) return;

    setState(() => _deleting = true);
    try {
      await ref.read(wordOsApiProvider).deleteWord(wordId);
      if (!mounted) return;

      ScaffoldMessenger.of(
        context,
      ).showSnackBar(SnackBar(content: Text(s.deleteWordDone)));
      Navigator.of(context).maybePop();
    } catch (rawError) {
      final e = ApiException.from(rawError);
      if (!mounted) return;
      setState(() => _deleting = false);
      ScaffoldMessenger.of(
        context,
      ).showSnackBar(SnackBar(content: Text(s.apiError(e.code, e.message))));
    }
  }

  @override
  Widget build(BuildContext context) {
    final s = ref.watch(stringsProvider);
    final detail = ref.watch(wordDetailProvider(wordId));

    return Scaffold(
      appBar: AppBar(
        title: Text(s.wordJourney),
        actions: [
          // Only once the word has loaded: there is nothing to confirm the
          // deletion of, and nothing to name in the question, until then.
          if (detail.valueOrNull case final data?)
            IconButton(
              tooltip: s.deleteWord,
              icon: const Icon(Icons.delete_outline_rounded),
              color: context.palette.danger,
              onPressed: _deleting ? null : () => _confirmDelete(data.word),
            ),
        ],
      ),
      body: detail.when(
        loading: () => BusyView(message: s.loading),
        error: (e, _) => ErrorView.from(
          e,
          s,
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
                          child: Text(
                            word.text,
                            style: context.text.headlineSmall,
                          ),
                        ),
                        WordSpeakerButtons(
                          id: 'word-detail:${word.id}',
                          text: word.text,
                          size: 24,
                        ),
                        LevelBadge(label: word.cefrLevel.label),
                      ],
                    ),
                    const SizedBox(height: AppSpacing.xxs),

                    // The grammar of the entry, said plainly (ADR-056): what
                    // kind of word it is, and which form — the two questions a
                    // learner looking at `went` in their own list cannot
                    // otherwise answer.
                    Wrap(
                      spacing: AppSpacing.xxs,
                      runSpacing: AppSpacing.xxs,
                      children: [
                        if (word.partOfSpeech.trim().isNotEmpty)
                          StatusPill(
                            label: s.partOfSpeechLabel(word.partOfSpeech),
                            color: context.colors.primary,
                          ),
                        if (s.wordFormLabel(word.form) case final form?)
                          StatusPill(
                            label: form,
                            color: context.palette.warning,
                          ),
                      ],
                    ),
                    const SizedBox(height: AppSpacing.xxs),

                    // The meaning, and the way to change it (ADR-101). Beside
                    // the meaning rather than in the app bar: it is the one
                    // thing on this screen the learner owns outright, and the
                    // control belongs next to the thing it edits.
                    Row(
                      children: [
                        Expanded(
                          child: Text(
                            word.meaning,
                            textDirection: TextDirection.rtl,
                            style: context.text.titleMedium?.copyWith(
                              color: context.colors.primary,
                            ),
                          ),
                        ),
                        if (_savingMeaning)
                          const Padding(
                            padding: EdgeInsets.all(AppSpacing.xs),
                            child: SizedBox(
                              width: 18,
                              height: 18,
                              child: CircularProgressIndicator(
                                strokeWidth: 2.2,
                              ),
                            ),
                          )
                        else
                          IconButton(
                            tooltip: s.editMeaning,
                            icon: const Icon(Icons.edit_outlined, size: 20),
                            onPressed: _deleting
                                ? null
                                : () => _editMeaning(word),
                          ),
                      ],
                    ),
                    if (word.definitionEn.isNotEmpty) ...[
                      const SizedBox(height: AppSpacing.xs),
                      // English, laid out left-to-right so the full stop
                      // stays at the end (see add_word_screen).
                      EnglishText(
                        word.definitionEn,
                        textAlign: EnglishText.interfaceStart(context),
                        style: context.text.bodySmall?.copyWith(
                          color: context.colors.onSurface.withValues(
                            alpha: 0.6,
                          ),
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
                          color: context.colors.onSurface.withValues(
                            alpha: 0.55,
                          ),
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
      // Reachable only in the Owner's journey view: a learner's own detail
      // screen closes the moment they delete the word (ADR-071).
      WordEventType.deleted => s.stateLabel(WordState.deleted),
      WordEventType.meaningChanged => s.wordEventLabel(event.type),
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
