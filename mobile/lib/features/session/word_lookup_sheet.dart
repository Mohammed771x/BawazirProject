import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_providers.dart';
import '../../core/api/wordos_api.dart';
import '../../core/l10n/app_strings.dart';
import '../../core/models/models.dart';
import '../../core/theme/app_tokens.dart';
import '../../core/widgets/app_widgets.dart';
import '../../core/widgets/speaker_button.dart';

/// What appears when a learner taps a word inside a passage (Part 2 §17–§19).
///
/// Two different sheets, deliberately:
///
/// * **A target word** — pronounced, and nothing more. The session is about to
///   ask what it means; printing the answer here would turn the test into a
///   reading exercise.
/// * **Any other word** — pronounced *and* explained, with the option to add it
///   to the pipeline on the spot (§20). A passage is where unknown words are
///   actually met, so it is the natural place to collect them; making the
///   learner remember the word and retype it later loses most of them.
///
/// Nothing here is decided on the device: the spelling is resolved by the
/// server, and adding a word posts the sense id it returned (rule R1, ADR-012).
Future<void> showWordLookup(
  BuildContext context, {
  required String word,
  required bool isTarget,
  required Color color,
  GlossaryEntry? inContext,
}) {
  return showModalBottomSheet<void>(
    context: context,
    isScrollControlled: true,
    showDragHandle: true,
    builder: (_) => _WordLookupSheet(
      word: word,
      isTarget: isTarget,
      color: color,
      inContext: inContext,
    ),
  );
}

class _WordLookupSheet extends ConsumerStatefulWidget {
  const _WordLookupSheet({
    required this.word,
    required this.isTarget,
    required this.color,
    this.inContext,
  });

  final String word;
  final bool isTarget;
  final Color color;

  /// What this word means *in this passage*, written by the generator as it
  /// composed the sentence. When present nothing is fetched: the answer is
  /// already here, and it is a better answer than a dictionary can give.
  final GlossaryEntry? inContext;

  @override
  ConsumerState<_WordLookupSheet> createState() => _WordLookupSheetState();
}

class _WordLookupSheetState extends ConsumerState<_WordLookupSheet> {
  Future<WordDefinition>? _definition;
  final _added = <String>{};
  String? _error;
  bool _saving = false;

  @override
  void initState() {
    super.initState();
    // Two reasons to ask the server nothing:
    //
    // * a target word shows only the speaker, so a lookup would be spent on an
    //   answer that is deliberately hidden;
    // * a glossed word already has its meaning *in this sentence*, which is
    //   what the learner asked and what a dictionary cannot narrow down.
    if (!widget.isTarget && widget.inContext == null) {
      _definition = ref.read(wordOsApiProvider).defineWord(widget.word);
    }
  }

  Future<void> _add(WordCandidate candidate) async {
    final s = ref.read(stringsProvider);
    setState(() {
      _saving = true;
      _error = null;
    });
    try {
      await ref.read(wordOsApiProvider).addWord(candidate);
      if (mounted) setState(() => _added.add(candidate.senseId ?? candidate.meaning));
    } on ApiException catch (e) {
      // A word the learner already owns is the common case here — they met it
      // in a passage precisely because it is one of theirs — so it reads as a
      // statement of fact rather than a failure.
      if (mounted) {
        setState(() => _error =
            e.code == 'WORD_ALREADY_ADDED' ? s.alreadyInYourWords : e.message);
      }
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final s = ref.watch(stringsProvider);

    return SafeArea(
      child: Padding(
        padding: const EdgeInsets.fromLTRB(
            AppSpacing.md, 0, AppSpacing.md, AppSpacing.md),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Expanded(
                  child: Directionality(
                    // The word is English wherever the interface is not.
                    textDirection: TextDirection.ltr,
                    child: Text(
                      widget.word,
                      style: context.text.headlineSmall
                          ?.copyWith(color: widget.color),
                    ),
                  ),
                ),
                SpeakerButton(
                  id: 'lookup:${widget.word}',
                  text: widget.word,
                  size: 26,
                  color: widget.color,
                ),
              ],
            ),
            const SizedBox(height: AppSpacing.sm),
            if (widget.isTarget)
              _Note(text: s.targetWordNoMeaning)
            else if (widget.inContext != null)
              _inContextBody(s, widget.inContext!)
            else
              _definitionBody(s),
          ],
        ),
      ),
    );
  }

  /// The one meaning that matters: what the word means in this sentence.
  Widget _inContextBody(AppStrings s, GlossaryEntry entry) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      mainAxisSize: MainAxisSize.min,
      children: [
        AppCard(
          color: context.palette.subtleSurface,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  Text(
                    s.meaningHere,
                    style: context.text.labelSmall?.copyWith(
                      color: context.colors.onSurface.withValues(alpha: 0.6),
                    ),
                  ),
                  const Spacer(),
                  // What kind of word it is — "will" as an auxiliary is a
                  // different thing to learn than "will" as a noun.
                  if (entry.partOfSpeech.isNotEmpty)
                    StatusPill(
                      label: s.partOfSpeechLabel(entry.partOfSpeech),
                      color: widget.color,
                    ),
                ],
              ),
              const SizedBox(height: AppSpacing.xxs),
              Text(entry.meaning, style: context.text.titleMedium),
            ],
          ),
        ),
        const SizedBox(height: AppSpacing.sm),
        if (_added.isNotEmpty)
          StatusPill(
            label: s.wordAdded,
            color: context.palette.success,
            icon: Icons.check_rounded,
          )
        else
          Align(
            alignment: AlignmentDirectional.centerStart,
            child: FilledButton.tonalIcon(
              onPressed: _saving ? null : () => _addInContext(entry),
              icon: const Icon(Icons.add_rounded, size: 18),
              label: Text(s.addToMyWords),
            ),
          ),
        if (_error != null) ...[
          const SizedBox(height: AppSpacing.xs),
          _Note(text: _error!),
        ],
      ],
    );
  }

  /// Adds the word with the sense the passage actually used.
  ///
  /// The lexicon is still the source of truth for what may be added
  /// (ADR-012), so the sense list is fetched and the entry closest to the
  /// meaning the learner just read is chosen. Falling back to the first sense
  /// would file the word under a meaning they never saw.
  Future<void> _addInContext(GlossaryEntry entry) async {
    final s = ref.read(stringsProvider);
    setState(() {
      _saving = true;
      _error = null;
    });

    try {
      final definition =
          await ref.read(wordOsApiProvider).defineWord(widget.word);

      if (definition.senses.isEmpty) {
        if (mounted) setState(() => _error = s.noDictionaryEntry);
        return;
      }

      final chosen = _closestTo(entry.meaning, definition.senses);
      await ref.read(wordOsApiProvider).addWord(chosen);
      if (mounted) setState(() => _added.add(chosen.senseId ?? chosen.meaning));
    } on ApiException catch (e) {
      if (mounted) {
        setState(() => _error =
            e.code == 'WORD_ALREADY_ADDED' ? s.alreadyInYourWords : e.message);
      }
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  /// The lexicon sense whose meaning reads closest to the passage's.
  static WordCandidate _closestTo(
    String meaning,
    List<WordCandidate> senses,
  ) {
    final needle = meaning.trim();

    for (final sense in senses) {
      if (sense.meaning.trim() == needle) return sense;
    }
    // No exact match: prefer one that shares a word with it, then give up and
    // take the most common sense.
    for (final sense in senses) {
      if (sense.meaning.split(' ').any((w) =>
          w.length > 2 && needle.contains(w))) {
        return sense;
      }
    }
    return senses.first;
  }

  Widget _definitionBody(AppStrings s) {
    return FutureBuilder<WordDefinition>(
      future: _definition,
      builder: (context, snapshot) {
        if (snapshot.connectionState != ConnectionState.done) {
          return const Padding(
            padding: EdgeInsets.all(AppSpacing.md),
            child: Center(child: CircularProgressIndicator()),
          );
        }
        if (snapshot.hasError) {
          final error = snapshot.error;
          return _Note(
            text: error is ApiException ? error.message : s.somethingWentWrong,
          );
        }

        final definition = snapshot.data!;
        if (definition.isEmpty) return _Note(text: s.noDictionaryEntry);

        return Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisSize: MainAxisSize.min,
          children: [
            // "researching → research". Without this the sheet looks like it
            // answered a different question.
            if (definition.matchedText != null &&
                definition.matchedText!.toLowerCase() !=
                    widget.word.toLowerCase())
              Padding(
                padding: const EdgeInsets.only(bottom: AppSpacing.xs),
                child: Text(
                  s.fromWord(definition.matchedText!),
                  style: context.text.labelMedium?.copyWith(
                    color: context.colors.onSurface.withValues(alpha: 0.6),
                  ),
                ),
              ),
            ConstrainedBox(
              constraints: BoxConstraints(
                maxHeight: MediaQuery.of(context).size.height * 0.5,
              ),
              child: ListView(
                shrinkWrap: true,
                children: [
                  for (final sense in definition.senses)
                    Padding(
                      padding: const EdgeInsets.only(bottom: AppSpacing.xs),
                      child: _SenseTile(
                        sense: sense,
                        color: widget.color,
                        added: _added.contains(sense.senseId ?? sense.meaning),
                        onAdd: _saving ? null : () => _add(sense),
                      ),
                    ),
                ],
              ),
            ),
            if (_error != null) ...[
              const SizedBox(height: AppSpacing.xs),
              _Note(text: _error!),
            ],
          ],
        );
      },
    );
  }
}

class _SenseTile extends ConsumerWidget {
  const _SenseTile({
    required this.sense,
    required this.color,
    required this.added,
    required this.onAdd,
  });

  final WordCandidate sense;
  final Color color;
  final bool added;
  final VoidCallback? onAdd;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);

    return AppCard(
      color: context.palette.subtleSurface,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(sense.meaning, style: context.text.titleSmall),
          if (sense.definitionEn.isNotEmpty) ...[
            const SizedBox(height: AppSpacing.xxs),
            Text(
              sense.definitionEn,
              style: context.text.bodySmall?.copyWith(
                color: context.colors.onSurface.withValues(alpha: 0.7),
              ),
            ),
          ],
          const SizedBox(height: AppSpacing.xs),
          Row(
            children: [
              if (sense.partOfSpeech.isNotEmpty)
                StatusPill(label: sense.partOfSpeech, color: color),
              const Spacer(),
              if (added)
                StatusPill(
                  label: s.wordAdded,
                  color: context.palette.success,
                  icon: Icons.check_rounded,
                )
              else
                TextButton.icon(
                  onPressed: onAdd,
                  icon: const Icon(Icons.add_rounded, size: 18),
                  label: Text(s.addToMyWords),
                ),
            ],
          ),
        ],
      ),
    );
  }
}

class _Note extends StatelessWidget {
  const _Note({required this.text});

  final String text;

  @override
  Widget build(BuildContext context) {
    return AppCard(
      color: context.palette.subtleSurface,
      child: Text(
        text,
        style: context.text.bodyMedium?.copyWith(
          color: context.colors.onSurface.withValues(alpha: 0.75),
        ),
      ),
    );
  }
}
