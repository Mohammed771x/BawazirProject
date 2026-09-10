import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../app/router.dart';
import '../../core/api/api_providers.dart';
import '../../core/api/wordos_api.dart';
import '../../core/l10n/app_strings.dart';
import '../../core/models/models.dart';
import '../../core/theme/app_tokens.dart';
import '../../core/widgets/app_widgets.dart';
import 'word_widgets.dart';

/// One query for the whole screen: the words the learner owns, optionally
/// filtered by a search term.
final wordsProvider = FutureProvider.autoDispose.family<WordPage, String>((
  ref,
  query,
) {
  return ref.watch(wordOsApiProvider).words(query: query);
});

/// My Words (Part 2 §42–§46).
///
/// Deliberately one list. The pipeline states — Learning, Mature, Active,
/// Archived — are how the *system* thinks about a word, and splitting the
/// screen along them asked the learner to understand a state machine before
/// they could find a word they added last Tuesday. What a learner wants here is
/// their vocabulary and a way to search it; the state still shows on each row,
/// in words about learning rather than about the pipeline.
///
/// Nothing is hidden by the *system*: archived words are still listed, because
/// rule R8 forbids the system removing a word and disappearing from this screen
/// would look exactly like deletion.
///
/// The learner may remove one themselves (ADR-071), by swiping the row or from
/// the word's own screen. That is a different thing from the system deciding a
/// word is finished with, and it is the learner's list.
class VocabularyScreen extends ConsumerStatefulWidget {
  const VocabularyScreen({super.key});

  @override
  ConsumerState<VocabularyScreen> createState() => _VocabularyScreenState();
}

class _VocabularyScreenState extends ConsumerState<VocabularyScreen> {
  final _search = TextEditingController();
  Timer? _debounce;
  String _query = '';

  @override
  void dispose() {
    _debounce?.cancel();
    _search.dispose();
    super.dispose();
  }

  void _onSearchChanged(String value) {
    // Debounced: the search runs on the server, and a request per keystroke
    // would spend the learner's rate limit on typing.
    _debounce?.cancel();
    _debounce = Timer(const Duration(milliseconds: 300), () {
      if (mounted) setState(() => _query = value.trim());
    });
  }

  /// Confirms, deletes, and reports whether the row may go.
  ///
  /// Returns false on refusal *and* on failure, which is what keeps the list
  /// honest: the row stays exactly where it was unless the server actually
  /// removed the word.
  Future<bool> _delete(Word word) async {
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

    if (confirmed != true) return false;

    try {
      await ref.read(wordOsApiProvider).deleteWord(word.id);
      if (!mounted) return false;

      // Refetched rather than removed locally: the count in the header is the
      // server's, and so is the page this row came from (rule R1).
      ref.invalidate(wordsProvider(_query));

      ScaffoldMessenger.of(context)
          .showSnackBar(SnackBar(content: Text(s.deleteWordDone)));
      return true;
    } catch (rawError) {
      final e = ApiException.from(rawError);
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(content: Text(s.apiError(e.code, e.message))),
        );
      }
      return false;
    }
  }

  @override
  Widget build(BuildContext context) {
    final s = ref.watch(stringsProvider);
    final words = ref.watch(wordsProvider(_query));

    return Scaffold(
      appBar: AppBar(title: Text(s.myWords)),
      body: Column(
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(
                AppSpacing.md, AppSpacing.sm, AppSpacing.md, AppSpacing.xs),
            child: TextField(
              controller: _search,
              onChanged: _onSearchChanged,
              textInputAction: TextInputAction.search,
              decoration: InputDecoration(
                hintText: s.searchYourWords,
                prefixIcon: const Icon(Icons.search_rounded),
                suffixIcon: _search.text.isEmpty
                    ? null
                    : IconButton(
                        icon: const Icon(Icons.close_rounded),
                        onPressed: () {
                          _search.clear();
                          _onSearchChanged('');
                        },
                      ),
              ),
            ),
          ),
          Expanded(
            child: words.when(
              loading: () => BusyView(message: s.loading),
              error: (e, _) => ErrorView(
                message: s.somethingWentWrong,
                retryLabel: s.retry,
                onRetry: () => ref.invalidate(wordsProvider(_query)),
              ),
              data: (page) => _list(s, page),
            ),
          ),
        ],
      ),
    );
  }

  Widget _list(AppStrings s, WordPage page) {
    if (page.items.isEmpty) {
      return EmptyState(
        icon: _query.isEmpty ? Icons.inbox_rounded : Icons.search_off_rounded,
        title: _query.isEmpty ? s.noWordsYet : s.noWordsMatch,
        message: _query.isEmpty ? s.chooseMeaningSubtitle : null,
      );
    }

    return RefreshIndicator(
      onRefresh: () async => ref.refresh(wordsProvider(_query).future),
      child: ListView.separated(
        padding: const EdgeInsets.fromLTRB(
          AppSpacing.md,
          AppSpacing.xs,
          AppSpacing.md,
          // Clear the floating "Add word" button and the nav bar.
          AppSpacing.xxl * 2,
        ),
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
          final word = page.items[index - 1];
          return Dismissible(
            key: ValueKey(word.id),
            // One direction only. A list of vocabulary is read in both
            // directions depending on the interface language, and a row that
            // deletes whichever way it is pushed is a row that deletes by
            // accident.
            direction: DismissDirection.endToStart,
            background: _DeleteBackground(),
            // `confirmDismiss` rather than `onDismissed`: the row must not
            // animate away before the server has agreed, or a failed delete
            // leaves the learner looking at a list that lies to them.
            confirmDismiss: (_) => _delete(word),
            child: WordTile(
              word: word,
              // Refreshed on the way back rather than left to whoever changed
              // something over there to remember to invalidate this. The word's
              // own screen can delete it, and a list that still shows a word the
              // learner has just watched themselves delete is the worst of the
              // available outcomes.
              onTap: () async {
                await context.push(Routes.word(word.id));
                if (mounted) ref.invalidate(wordsProvider(_query));
              },
            ),
          );
        },
      ),
    );
  }
}


/// What shows behind a row being swiped away.
///
/// Red, and labelled. An unlabelled coloured panel is a guess; a learner who
/// has swiped far enough to see this should already know what letting go does.
class _DeleteBackground extends ConsumerWidget {
  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);

    return Container(
      alignment: AlignmentDirectional.centerEnd,
      padding: const EdgeInsetsDirectional.only(end: AppSpacing.lg),
      decoration: BoxDecoration(
        color: context.palette.danger,
        borderRadius: AppRadii.cardBorder,
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          const Icon(Icons.delete_outline_rounded, color: Colors.white),
          const SizedBox(width: AppSpacing.xs),
          Text(
            s.deleteWord,
            style: context.text.labelLarge?.copyWith(color: Colors.white),
          ),
        ],
      ),
    );
  }
}
