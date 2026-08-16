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
    } on ApiException catch (e) {
      if (mounted) _snack(e.message);
    } finally {
      if (mounted) setState(() => _searching = false);
    }
  }

  void _snack(String message) => ScaffoldMessenger.of(context)
      .showSnackBar(SnackBar(content: Text(message)));

  /// The learner may only ever pick a meaning the lexicon provided. There is no
  /// path to typing one: a hand-written meaning can be wrong, mistranslated, or
  /// attached to a string that is not English at all, and the whole learning
  /// pipeline is built on the meaning being trustworthy (demo review §16,
  /// ADR-012). The server enforces the same rule independently.
  Future<void> _select(WordCandidate candidate) async {
    setState(() => _saving = true);
    try {
      final word = await ref.read(wordOsApiProvider).addWord(candidate);
      if (mounted) {
        setState(() {
          _added = word;
          _candidates = const [];
          _notFoundQuery = null;
          _controller.clear();
        });
      }
    } on ApiException catch (e) {
      if (mounted) _snack(e.message);
    } finally {
      if (mounted) setState(() => _saving = false);
    }
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
                      const SizedBox(height: AppSpacing.md),
                      if (_candidates.isNotEmpty)
                        SectionHeader(title: s.spellingSuggestion),
                    ] else
                      SectionHeader(
                        title: _candidates
                                    .map((c) => c.text.toLowerCase())
                                    .toSet()
                                    .length >
                                1
                            // Several different words matched the prefix, so
                            // the learner is still choosing a word.
                            ? s.chooseWord
                            : s.chooseMeaning,
                        subtitle: s.chooseMeaningSubtitle,
                      ),
                    for (final candidate in _candidates) ...[
                      _CandidateTile(
                        candidate: candidate,
                        onTap: () => _select(candidate),
                      ),
                      const SizedBox(height: AppSpacing.xs),
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
