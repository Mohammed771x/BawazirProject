import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_providers.dart';
import '../../core/api/wordos_api.dart';
import '../../core/l10n/app_strings.dart';
import '../../core/models/models.dart';
import '../../core/theme/app_tokens.dart';
import '../../core/widgets/app_widgets.dart';

/// Add Word — one word at a time, with the intended meaning chosen explicitly
/// (Word Life Cycle §16–17). The AI classification and persistence happen on
/// the server; this screen only collects the choice.
class AddWordScreen extends ConsumerStatefulWidget {
  const AddWordScreen({super.key});

  @override
  ConsumerState<AddWordScreen> createState() => _AddWordScreenState();
}

class _AddWordScreenState extends ConsumerState<AddWordScreen> {
  /// Copy for the async paths, which have no build context to read it from.
  AppStrings get _strings => ref.read(stringsProvider);

  final _controller = TextEditingController();
  Timer? _debounce;

  List<WordCandidate> _candidates = const [];
  String? _notFoundQuery;
  bool _searching = false;
  bool _saving = false;
  Word? _added;

  @override
  void dispose() {
    _debounce?.cancel();
    _controller.dispose();
    super.dispose();
  }

  void _onChanged(String value) {
    _debounce?.cancel();
    if (value.trim().isEmpty) {
      setState(() {
        _candidates = const [];
        _notFoundQuery = null;
      });
      return;
    }
    _debounce = Timer(const Duration(milliseconds: 220), () => _search(value));
  }

  Future<void> _search(String value) async {
    setState(() => _searching = true);
    try {
      final results = await ref.read(wordOsApiProvider).lookupWord(value);
      if (!mounted) return;
      setState(() {
        _candidates = results;
        // Nothing exact came back, so the string is not a word we know. The
        // learner sees that plainly, with suggestions if we have any — never a
        // silent "add it anyway".
        _notFoundQuery =
            results.any((c) => !c.isSpellingSuggestion) ? null : value.trim();
      });
    } catch (rawError) {
      final e = ApiException.from(rawError);
      if (mounted) _snack(_strings.apiError(e.code, e.message));
    } finally {
      if (mounted) setState(() => _searching = false);
    }
  }

  void _snack(String message) => ScaffoldMessenger.of(context)
      .showSnackBar(SnackBar(content: Text(message)));

  /// Which candidate the "write your own" card goes after.
  ///
  /// A single index, computed once, because the obvious spelling of this —
  /// `i == 2 || i == last` — draws the card twice on every list longer than
  /// three.
  int get _writeYourOwnAfter =>
      _candidates.length > 3 ? 2 : _candidates.length - 1;

  /// The word the learner is looking at, or null while they are still choosing
  /// one.
  ///
  /// Two ways to be sure which word is meant, and the first matters more than
  /// it looks. Typing `sell` returns `sell`, `selling`, `seller` and `sell off`
  /// — four different words — so "every candidate shares one text" is false for
  /// the most ordinary search there is, and the offer to write a meaning
  /// silently never appeared. If what was typed *is* one of the words on
  /// offer, that is the word.
  ///
  /// A query the lexicon did not recognise still resolves — to itself
  /// (ADR-075). The dictionary is a machine join with holes in it, and refusing
  /// to let a learner write a meaning for a word that fell down one is refusing
  /// exactly the word they came here for.
  String? get _resolvedWord {
    if (_notFoundQuery != null) return _notFoundQuery;
    if (_candidates.isEmpty) return null;

    final typed = _controller.text.trim().toLowerCase();
    for (final candidate in _candidates) {
      if (candidate.text.toLowerCase() == typed) return candidate.text;
    }

    // Nothing typed matches exactly, but every result is the same word — a
    // prefix that only one word answers.
    final words = _candidates.map((c) => c.text.toLowerCase()).toSet();
    return words.length == 1 ? _candidates.first.text : null;
  }

  /// Adds the word with the lexicon's own meaning.
  Future<void> _select(WordCandidate candidate) =>
      _save(() => ref.read(wordOsApiProvider).addWord(candidate));

  /// The shared half: run it, show the result, clear the field.
  Future<void> _save(Future<Word> Function() add) async {
    setState(() => _saving = true);
    try {
      final word = await add();
      if (mounted) {
        setState(() {
          _added = word;
          _candidates = const [];
          _notFoundQuery = null;
          _controller.clear();
        });
      }
    } catch (rawError) {
      final e = ApiException.from(rawError);
      if (mounted) _snack(_strings.apiError(e.code, e.message));
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  /// Asks for a meaning in a sheet, and shows what the checker made of it.
  ///
  /// A sheet rather than a field wedged into the list: the learner is switching
  /// from *choosing* to *writing*, and the two want different room. It also
  /// keeps the suggestions on screen behind it, so backing out returns them to
  /// exactly where they were.
  ///
  /// The sheet does the saving itself (ADR-074). It has to: a rejected meaning
  /// is answered with what the checker would accept, and that conversation
  /// belongs beside the field they typed it in — closing the sheet to report it
  /// in a snackbar would throw away both their text and the suggestions.
  Future<void> _writeMeaningFor(String text) async {
    final saved = await showModalBottomSheet<Word>(
      context: context,
      isScrollControlled: true,
      showDragHandle: true,
      // Dismissing by tapping outside mid-check would leave a request in
      // flight with nowhere to report to.
      isDismissible: true,
      builder: (_) => _WriteMeaningSheet(word: text),
    );

    if (saved == null || !mounted) return;

    setState(() {
      _added = saved;
      _candidates = const [];
      _notFoundQuery = null;
      _controller.clear();
    });
  }

  @override
  Widget build(BuildContext context) {
    final s = ref.watch(stringsProvider);

    return Scaffold(
      appBar: AppBar(title: Text(s.addWord)),
      body: SafeArea(
        child: Column(
          children: [
            Padding(
              padding: AppSpacing.page,
              child: TextField(
                controller: _controller,
                autofocus: true,
                textInputAction: TextInputAction.search,
                decoration: InputDecoration(
                  hintText: s.typeWord,
                  prefixIcon: const Icon(Icons.search_rounded),
                  suffixIcon: _searching
                      ? const Padding(
                          padding: EdgeInsets.all(14),
                          child: SizedBox(
                            width: 18,
                            height: 18,
                            child: CircularProgressIndicator(strokeWidth: 2.2),
                          ),
                        )
                      : null,
                ),
                onChanged: _onChanged,
                onSubmitted: _search,
              ),
            ),
            if (_saving)
              Expanded(child: BusyView(message: s.analyzingWord))
            else if (_added != null)
              Expanded(child: _AddedView(word: _added!, onAddAnother: () {
                setState(() => _added = null);
              }))
            else if (_candidates.isEmpty && _notFoundQuery == null)
              Expanded(
                child: EmptyState(
                  icon: Icons.travel_explore_rounded,
                  title: s.typeWord,
                  message: s.chooseMeaningSubtitle,
                ),
              )
            else
              Expanded(
                child: ListView(
                  padding: AppSpacing.page,
                  children: [
                    if (_notFoundQuery != null) ...[
                      _NotFoundNotice(query: _notFoundQuery!),
                      const SizedBox(height: AppSpacing.sm),
                      // Above the spelling suggestions here, and below the
                      // meanings in the other branch. The order follows what is
                      // most likely to be right: when the word is known, its
                      // own meanings are; when it is not, writing one is — and
                      // a suggestion list for an unrecognised string is a list
                      // of guesses.
                      _WriteYourOwnCard(
                        word: _notFoundQuery!,
                        unknownWord: true,
                        onTap: () => _writeMeaningFor(_notFoundQuery!),
                      ),
                      const SizedBox(height: AppSpacing.md),
                      if (_candidates.isNotEmpty)
                        SectionHeader(title: s.spellingSuggestion),
                    ] else
                      SectionHeader(
                        title: _resolvedWord == null
                            // Several different words matched the prefix, so
                            // the learner is still choosing a word.
                            ? s.chooseWord
                            : s.chooseMeaning,
                        subtitle: s.chooseMeaningSubtitle,
                      ),
                    for (var i = 0; i < _candidates.length; i++) ...[
                      _CandidateTile(
                        candidate: _candidates[i],
                        onTap: () => _select(_candidates[i]),
                      ),
                      const SizedBox(height: AppSpacing.xs),

                      // After the first few meanings, not after all of them.
                      // `sell` returns more than twenty rows, and a card at the
                      // foot of that is a card nobody reaches — which is the
                      // same as not having built it. Still *below* real
                      // meanings, because the lexicon's answer is the one to
                      // try first.
                      if (i == _writeYourOwnAfter)
                        if (_resolvedWord case final word?) ...[
                          const SizedBox(height: AppSpacing.xs),
                          _WriteYourOwnCard(
                            word: word,
                            onTap: () => _writeMeaningFor(word),
                          ),
                          const SizedBox(height: AppSpacing.sm),
                        ],
                    ],
                  ],
                ),
              ),
          ],
        ),
      ),
    );
  }
}

/// Shown when the typed string is not in the lexicon. It explains *why* the
/// word is refused rather than just failing, so the learner does not read it as
/// a bug.
class _NotFoundNotice extends ConsumerWidget {
  const _NotFoundNotice({required this.query});

  final String query;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    return AppCard(
      color: context.palette.warningSurface,
      borderColor: context.palette.warning.withValues(alpha: 0.35),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Icon(Icons.search_off_rounded, color: context.palette.warning),
          const SizedBox(width: AppSpacing.sm),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(s.wordNotFound(query), style: context.text.titleSmall),
                const SizedBox(height: AppSpacing.xxs),
                Text(
                  s.wordNotFoundBody,
                  style: context.text.bodySmall?.copyWith(
                    color: context.colors.onSurface.withValues(alpha: 0.7),
                  ),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

class _CandidateTile extends ConsumerWidget {
  const _CandidateTile({required this.candidate, required this.onTap});

  final WordCandidate candidate;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    return AppCard(
      onTap: onTap,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          if (candidate.isSpellingSuggestion)
            Padding(
              padding: const EdgeInsets.only(bottom: AppSpacing.xs),
              child: StatusPill(
                label: s.spellingSuggestion,
                color: context.palette.warning,
                icon: Icons.auto_fix_high_rounded,
              ),
            ),
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(candidate.text, style: context.text.titleMedium),
                    if (candidate.partOfSpeech.isNotEmpty)
                      Text(
                        candidate.partOfSpeech,
                        style: context.text.labelSmall?.copyWith(
                          color:
                              context.colors.onSurface.withValues(alpha: 0.55),
                        ),
                      ),
                  ],
                ),
              ),
              LevelBadge(label: candidate.suggestedLevel.label),
            ],
          ),
          if (candidate.meaning.isNotEmpty) ...[
            const SizedBox(height: AppSpacing.xs),
            Align(
              alignment: AlignmentDirectional.centerStart,
              child: Text(
                candidate.meaning,
                textDirection: TextDirection.rtl,
                style: context.text.titleSmall
                    ?.copyWith(color: context.colors.primary),
              ),
            ),
          ],
          if (candidate.definitionEn.isNotEmpty) ...[
            const SizedBox(height: AppSpacing.xxs),
            Text(
              candidate.definitionEn,
              style: context.text.bodySmall?.copyWith(
                color: context.colors.onSurface.withValues(alpha: 0.6),
              ),
            ),
          ],
        ],
      ),
    );
  }
}

class _AddedView extends ConsumerWidget {
  const _AddedView({required this.word, required this.onAddAnother});

  final Word word;
  final VoidCallback onAddAnother;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    return Padding(
      padding: const EdgeInsets.all(AppSpacing.md),
      child: Column(
        children: [
          const Spacer(),
          Icon(Icons.check_circle_rounded,
              size: 56, color: context.palette.success),
          const SizedBox(height: AppSpacing.md),
          Text(word.text, style: context.text.headlineSmall),
          const SizedBox(height: AppSpacing.xxs),
          Text(
            word.meaning,
            textDirection: TextDirection.rtl,
            style: context.text.titleMedium
                ?.copyWith(color: context.colors.primary),
          ),
          const SizedBox(height: AppSpacing.sm),
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: AppSpacing.lg),
            child: Column(
              children: [
                Text(
                  s.wordAdded,
                  textAlign: TextAlign.center,
                  style: context.text.bodyMedium?.copyWith(
                    color: context.colors.onSurface.withValues(alpha: 0.65),
                  ),
                ),
                const SizedBox(height: AppSpacing.xs),
                StatusPill(
                  label: s.skillName(word.currentSkill ?? SkillType.reading),
                  color: context.palette.success,
                  icon: Icons.playlist_add_check_rounded,
                ),
              ],
            ),
          ),
          const Spacer(),
          FilledButton(onPressed: onAddAnother, child: Text(s.addWord)),
          const SizedBox(height: AppSpacing.xs),
          OutlinedButton(
            onPressed: () => Navigator.of(context).maybePop(),
            child: Text(s.done),
          ),
        ],
      ),
    );
  }
}


/// The way out of the dictionary's answer, offered under it.
///
/// Under, not above: the lexicon's meanings are still the first thing to try,
/// and a learner who finds the right one there should take it. This is for the
/// case the list does not cover — which, for a machine-joined lexicon, is more
/// often than anybody would like.
class _WriteYourOwnCard extends ConsumerWidget {
  const _WriteYourOwnCard({
    required this.word,
    required this.onTap,
    this.unknownWord = false,
  });

  final String word;
  final VoidCallback onTap;

  /// Whether the dictionary has never heard of this word (ADR-075).
  ///
  /// Only the wording changes. Under a list of meanings this is "write your
  /// own"; under "not in the dictionary" it has to name the word, because it is
  /// the only way forward on that screen rather than one of several.
  final bool unknownWord;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);

    return AppCard(
      onTap: onTap,
      color: context.palette.subtleSurface,
      child: Row(
        children: [
          Icon(Icons.edit_note_rounded, color: context.colors.primary),
          const SizedBox(width: AppSpacing.sm),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  unknownWord ? s.addItAnyway(word) : s.writeMeaningYourself,
                  style: context.text.titleSmall,
                ),
                const SizedBox(height: AppSpacing.xxs),
                Text(
                  s.writeMeaningSubtitle,
                  style: context.text.bodySmall?.copyWith(
                    color: context.colors.onSurface.withValues(alpha: 0.65),
                  ),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

/// Where the learner writes what the word means to them (ADR-072), and where
/// the checker answers (ADR-074).
///
/// Pops the saved [Word], or null if they backed out. It saves rather than
/// handing the text back because a rejection is a conversation: the checker
/// says what it thinks the word means and offers meanings it would accept, and
/// that has to appear beside the field the learner typed in — with their text
/// still in it.
class _WriteMeaningSheet extends ConsumerStatefulWidget {
  const _WriteMeaningSheet({required this.word});

  final String word;

  @override
  ConsumerState<_WriteMeaningSheet> createState() => _WriteMeaningSheetState();
}

class _WriteMeaningSheetState extends ConsumerState<_WriteMeaningSheet> {
  final _controller = TextEditingController();

  bool _saving = false;

  /// The word being added, which is not always the word the sheet opened on.
  ///
  /// The checker may say the English is misspelled (ADR-075), and taking its
  /// spelling has to change what is *saved*, not merely what is displayed —
  /// otherwise tapping "use this spelling" sends the same wrong word again.
  late String _word = widget.word;

  /// The checker's objection to what is currently in the field, or null.
  ///
  /// Cleared the moment they edit: an objection to text they have since changed
  /// is just noise, and leaving it there makes the sheet look broken.
  MeaningRejectedException? _rejection;

  /// The checker's objection to the *word*, which is a different conversation.
  ///
  /// It has no "keep mine": a meaning is the learner's to insist on and a
  /// spelling is not, because nothing downstream can teach a string that is not
  /// a word.
  WordRejectedException? _wordRejection;

  /// Anything else that went wrong, already localized.
  String? _error;

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  Future<void> _submit({bool acceptAnyway = false}) async {
    final meaning = _controller.text.trim();
    if (meaning.isEmpty || _saving) return;

    final s = ref.read(stringsProvider);
    setState(() {
      _saving = true;
      _error = null;
      _wordRejection = null;
      if (!acceptAnyway) _rejection = null;
    });

    try {
      final word = await ref.read(wordOsApiProvider).addWordWithMeaning(
            text: _word,
            meaning: meaning,
            acceptAnyway: acceptAnyway,
          );
      if (mounted) Navigator.of(context).pop(word);
    } on WordRejectedException catch (rejection) {
      // The English is the problem, not the Arabic. Shown as its own thing so
      // the learner does not go back and rewrite the half that was right.
      if (mounted) setState(() => _wordRejection = rejection);
    } on MeaningRejectedException catch (rejection) {
      // Not a failure — the checker's answer. Their text stays exactly where
      // it is, with the suggestions underneath it.
      if (mounted) setState(() => _rejection = rejection);
    } catch (rawError) {
      final e = ApiException.from(rawError);
      if (mounted) setState(() => _error = s.apiError(e.code, e.message));
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  /// Takes the checker's spelling of the English word and tries again.
  void _useSpelling(String spelling) {
    setState(() {
      _word = spelling;
      _wordRejection = null;
    });
    _submit();
  }

  /// Takes one of the checker's suggestions and saves it.
  ///
  /// Sent as a fresh meaning rather than as an override: it is the checker's
  /// own wording, so it will be accepted, and it is recorded as approved —
  /// which is true, and is what makes `Overridden` mean something.
  void _useSuggestion(String suggestion) {
    _controller.text = suggestion;
    setState(() => _rejection = null);
    _submit();
  }

  @override
  Widget build(BuildContext context) {
    final s = ref.watch(stringsProvider);
    final canSave = _controller.text.trim().isNotEmpty && !_saving;

    return Padding(
      padding: EdgeInsets.only(
        left: AppSpacing.md,
        right: AppSpacing.md,
        bottom: MediaQuery.of(context).viewInsets.bottom + AppSpacing.md,
      ),
      child: SingleChildScrollView(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            // The word stays in front of them while they write, left-to-right
            // wherever the interface is not.
            Directionality(
              textDirection: TextDirection.ltr,
              child: Text(_word, style: context.text.headlineSmall),
            ),
            const SizedBox(height: AppSpacing.xxs),
            Text(
              s.writeMeaningTitle,
              style: context.text.labelMedium?.copyWith(
                color: context.colors.onSurface.withValues(alpha: 0.65),
              ),
            ),
            const SizedBox(height: AppSpacing.md),
            TextField(
              controller: _controller,
              autofocus: true,
              enabled: !_saving,
              // Arabic, because that is what the server accepts and what every
              // skill will mark answers against.
              textDirection: TextDirection.rtl,
              textInputAction: TextInputAction.done,
              maxLength: 256,
              decoration: InputDecoration(hintText: s.meaningFieldHint),
              onSubmitted: (_) => _submit(),
              onChanged: (_) => setState(() {
                // Their edit answers the objection; keeping it would be the
                // sheet arguing with text that no longer exists.
                _rejection = null;
                _error = null;
                // Not `_wordRejection`: that one is about the English above the
                // field, which editing the Arabic does not change.
              }),
            ),

            if (_wordRejection case final rejection?) ...[
              const SizedBox(height: AppSpacing.xs),
              _WordVerdict(
                rejection: rejection,
                onUse: _saving ? null : _useSpelling,
              ),
              const SizedBox(height: AppSpacing.sm),
              // No "save it anyway" beneath this one, deliberately. The
              // learner may overrule a meaning (ADR-074); they may not
              // overrule "that is not a word", because five sessions would
              // then be spent teaching a typo.
            ] else if (_rejection case final rejection?) ...[
              const SizedBox(height: AppSpacing.xs),
              _CheckerVerdict(
                rejection: rejection,
                onUse: _saving ? null : _useSuggestion,
              ),
              const SizedBox(height: AppSpacing.sm),
              // Below the suggestions, and quieter than them: the checker is
              // usually right, and the learner is allowed to know better
              // (ADR-074).
              Align(
                alignment: AlignmentDirectional.centerStart,
                child: TextButton(
                  onPressed:
                      _saving ? null : () => _submit(acceptAnyway: true),
                  child: Text(s.keepMyMeaning),
                ),
              ),
            ] else ...[
              const SizedBox(height: AppSpacing.xs),
              Text(
                s.meaningNeedsRealWord,
                style: context.text.bodySmall?.copyWith(
                  color: context.colors.onSurface.withValues(alpha: 0.6),
                ),
              ),
            ],

            if (_error != null) ...[
              const SizedBox(height: AppSpacing.xs),
              AppCard(
                color: context.palette.warningSurface,
                child: Text(_error!, style: context.text.bodyMedium),
              ),
            ],

            const SizedBox(height: AppSpacing.md),
            SizedBox(
              width: double.infinity,
              child: FilledButton(
                // Disabled rather than failing on an empty meaning: the refusal
                // would be the app telling the learner off for pressing the
                // button it just offered them.
                onPressed: canSave ? () => _submit() : null,
                child: _saving
                    ? Row(
                        // `min` and a `Flexible` label: a full-width Row inside
                        // a button overflows the moment the label is long,
                        // which "يتحقق من المعنى…" is on a narrow phone.
                        mainAxisSize: MainAxisSize.min,
                        mainAxisAlignment: MainAxisAlignment.center,
                        children: [
                          const SizedBox(
                            width: 16,
                            height: 16,
                            child: CircularProgressIndicator(strokeWidth: 2),
                          ),
                          const SizedBox(width: AppSpacing.sm),
                          Flexible(
                            child: Text(
                              s.checkingMeaning,
                              overflow: TextOverflow.ellipsis,
                            ),
                          ),
                        ],
                      )
                    : Text(s.saveWord),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/// What the checker said, and what it would accept instead.
class _CheckerVerdict extends ConsumerWidget {
  const _CheckerVerdict({required this.rejection, required this.onUse});

  final MeaningRejectedException rejection;
  final void Function(String suggestion)? onUse;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);

    return AppCard(
      color: context.palette.warningSurface,
      borderColor: context.palette.warning.withValues(alpha: 0.35),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Icon(
                // A misspelling and a wrong meaning are different news, and the
                // icon should not tell the learner they got it wrong when they
                // got it right and mistyped it.
                rejection.isSpellingOnly
                    ? Icons.spellcheck_rounded
                    : Icons.info_outline_rounded,
                size: 18,
                color: context.palette.warning,
              ),
              const SizedBox(width: AppSpacing.xs),
              Text(s.meaningLooksWrong, style: context.text.titleSmall),
            ],
          ),
          const SizedBox(height: AppSpacing.xxs),
          // The checker's own sentence, in the learner's language. Shown as
          // written: there is no canned string for what is wrong with *this*
          // meaning, which is the whole reason for asking.
          Text(rejection.message, style: context.text.bodyMedium),

          if (rejection.suggestions.isNotEmpty) ...[
            const SizedBox(height: AppSpacing.sm),
            Text(
              s.meaningSuggestions,
              style: context.text.labelMedium?.copyWith(
                color: context.colors.onSurface.withValues(alpha: 0.7),
              ),
            ),
            const SizedBox(height: AppSpacing.xxs),
            Wrap(
              spacing: AppSpacing.xs,
              runSpacing: AppSpacing.xs,
              children: [
                for (final suggestion in rejection.suggestions)
                  ActionChip(
                    label: Text(suggestion),
                    onPressed:
                        onUse == null ? null : () => onUse!(suggestion),
                  ),
              ],
            ),
          ],
        ],
      ),
    );
  }
}

/// What the checker said about the English word itself (ADR-075).
///
/// Sibling of [_CheckerVerdict] and deliberately its own widget rather than a
/// flag on it. They are two different pieces of news — "that is not what this
/// word means" and "that is not a word" — and the second one offers a spelling
/// rather than a meaning, with no way to insist underneath it.
class _WordVerdict extends ConsumerWidget {
  const _WordVerdict({required this.rejection, required this.onUse});

  final WordRejectedException rejection;
  final void Function(String spelling)? onUse;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    final corrected = rejection.correctedWord;

    return AppCard(
      color: context.palette.warningSurface,
      borderColor: context.palette.warning.withValues(alpha: 0.35),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Icon(Icons.spellcheck_rounded,
                  size: 18, color: context.palette.warning),
              const SizedBox(width: AppSpacing.xs),
              Text(s.wordLooksWrong, style: context.text.titleSmall),
            ],
          ),
          const SizedBox(height: AppSpacing.xxs),
          // The checker's own sentence, in the learner's language.
          Text(rejection.message, style: context.text.bodyMedium),

          if (corrected != null && corrected.isNotEmpty) ...[
            const SizedBox(height: AppSpacing.sm),
            Text(
              s.didYouMeanWord(corrected),
              style: context.text.labelMedium?.copyWith(
                color: context.colors.onSurface.withValues(alpha: 0.7),
              ),
            ),
            const SizedBox(height: AppSpacing.xxs),
            Align(
              alignment: AlignmentDirectional.centerStart,
              child: ActionChip(
                // Left-to-right whatever the interface is: it is an English
                // word, and the Arabic layout would otherwise reverse it.
                label: Directionality(
                  textDirection: TextDirection.ltr,
                  child: Text(corrected),
                ),
                avatar: const Icon(Icons.auto_fix_high_rounded, size: 18),
                onPressed: onUse == null ? null : () => onUse!(corrected),
                tooltip: s.useThisSpelling,
              ),
            ),
          ],
        ],
      ),
    );
  }
}
