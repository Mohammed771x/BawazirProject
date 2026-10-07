import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../app/router.dart';
import '../../core/api/api_providers.dart';
import '../../core/api/server_revision.dart';
import '../../core/api/wordos_api.dart';
import '../../core/l10n/app_strings.dart';
import '../../core/models/models.dart';
import '../../core/theme/app_tokens.dart';
import '../../core/widgets/app_widgets.dart';
import 'word_widgets.dart';

/// The words the learner owns, optionally filtered by a search term — loaded a
/// page at a time as the learner scrolls (ADR-127).
///
/// This used to read one page and stop. The server has always paged (Part 3
/// §37), so a learner with 153 words was told "153 words" and shown fifty, and
/// the other hundred were simply unreachable.
final wordsProvider = AsyncNotifierProvider.autoDispose
    .family<WordListNotifier, WordList, String>(WordListNotifier.new);

/// What My Words has loaded so far.
class WordList {
  const WordList({
    required this.items,
    required this.total,
    required this.hasMore,
    this.loadingMore = false,
    this.loadMoreFailed = false,
  });

  final List<Word> items;

  /// Every match on the server, not just the rows loaded — the count the
  /// learner is shown.
  final int total;

  final bool hasMore;
  final bool loadingMore;

  /// The last attempt to load more failed. The list stops asking on its own,
  /// so a dead connection is one failed request rather than one per frame,
  /// and offers a retry instead.
  final bool loadMoreFailed;

  WordList copyWith({
    List<Word>? items,
    int? total,
    bool? hasMore,
    bool? loadingMore,
    bool? loadMoreFailed,
  }) => WordList(
    items: items ?? this.items,
    total: total ?? this.total,
    hasMore: hasMore ?? this.hasMore,
    loadingMore: loadingMore ?? this.loadingMore,
    loadMoreFailed: loadMoreFailed ?? this.loadMoreFailed,
  );
}

class WordListNotifier
    extends AutoDisposeFamilyAsyncNotifier<WordList, String> {
  /// The first screenful, then smaller steps as the learner scrolls. A
  /// learner opening the list mostly wants the words they added recently, so
  /// nobody pays for rows they never scroll to.
  static const firstPageSize = 20;
  static const nextPageSize = 10;

  /// The server's cap on one page.
  static const _maxPageSize = 100;

  /// How deep the learner had scrolled. Survives a rebuild — the notifier
  /// does, only `build` runs again.
  int _depth = firstPageSize;

  @override
  Future<WordList> build(String query) async {
    // Every write anywhere refetches this, and My Words stays alive in its
    // tab while the learner practises. So a refetch reloads what the learner
    // had scrolled to, bounded by one server page: deleting a word forty rows
    // down keeps them forty rows down, and a session answered elsewhere
    // costs at most one page, however far they once scrolled.
    refetchWhenServerChanges(ref);

    final size = _depth.clamp(firstPageSize, _maxPageSize);
    final page = await ref
        .watch(wordOsApiProvider)
        .words(query: query, offset: 0, pageSize: size);

    _depth = page.items.length;
    return WordList(
      items: page.items,
      total: page.total,
      hasMore: page.hasMore,
    );
  }

  /// The next rows, if there are any and nothing is already fetching.
  Future<void> loadMore() async {
    final current = state.valueOrNull;
    if (current == null ||
        !current.hasMore ||
        current.loadingMore ||
        state.isLoading) {
      return;
    }

    state = AsyncData(
      current.copyWith(loadingMore: true, loadMoreFailed: false),
    );

    try {
      final next = await ref
          .read(wordOsApiProvider)
          .words(
            query: arg,
            offset: current.items.length,
            pageSize: nextPageSize,
          );

      // A refetch landed while this was in flight; its list is newer, and
      // appending to it from the old offset would duplicate or skip rows.
      if (state.isLoading || state.valueOrNull?.loadingMore != true) return;

      // A word added elsewhere in the meantime shifts every row down by one,
      // so the first of these may already be on screen.
      final seen = {for (final w in current.items) w.id};
      final items = [
        ...current.items,
        ...next.items.where((w) => !seen.contains(w.id)),
      ];

      _depth = items.length;
      state = AsyncData(WordList(
        items: items,
        total: next.total,
        // A step that brought nothing new ends the list. Otherwise a server
        // that ignored `offset` — one older than ADR-127 — would answer the
        // first page every time, and the footer would ask again for ever.
        hasMore: next.hasMore && items.length > current.items.length,
      ));
    } catch (_) {
      final latest = state.valueOrNull;
      if (latest == null || state.isLoading) return;
      state = AsyncData(
        latest.copyWith(loadingMore: false, loadMoreFailed: true),
      );
    }
  }

  /// Takes a word the server has just deleted off the list at once.
  ///
  /// The refetch the delete triggers would remove it too, but a swiped row
  /// has to leave the tree the moment it is dismissed, not a round trip later.
  void removeLocally(String wordId) {
    final current = state.valueOrNull;
    if (current == null) return;

    final items = [
      for (final w in current.items)
        if (w.id != wordId) w,
    ];
    if (items.length == current.items.length) return;

    state = AsyncData(current.copyWith(items: items, total: current.total - 1));
  }
}

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
      ref.read(wordsProvider(_query).notifier).removeLocally(word.id);

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
              // A refetch keeps the list on screen. Every write anywhere
              // refetches it, and blanking the learner's place in a long list
              // for each one would undo the scrolling they did.
              skipLoadingOnReload: true,
              loading: () => BusyView(message: s.loading),
              error: (e, _) => ErrorView.from(
                e,
                s,
                onRetry: () => ref.invalidate(wordsProvider(_query)),
              ),
              data: (page) => _list(s, page),
            ),
          ),
        ],
      ),
    );
  }

  Widget _list(AppStrings s, WordList page) {
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
        // The count, the rows, and — while there is more — a footer.
        itemCount: page.items.length + (page.hasMore ? 2 : 1),
        separatorBuilder: (_, _) => const SizedBox(height: AppSpacing.xs),
        itemBuilder: (context, index) {
          if (index == page.items.length + 1) return _footer(s, page);
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
              onTap: () => context.push(Routes.word(word.id)),
            ),
          );
        },
      ),
    );
  }

  /// The end of what is loaded.
  ///
  /// Being built is the trigger: the list builds a little past what is on
  /// screen, so this appears as the learner nears the bottom — and at once
  /// when the first page does not fill the screen, where there would be
  /// nothing to scroll.
  Widget _footer(AppStrings s, WordList page) {
    if (page.loadMoreFailed) {
      return Center(
        child: TextButton.icon(
          icon: const Icon(Icons.refresh_rounded),
          label: Text(s.retry),
          onPressed: () => ref.read(wordsProvider(_query).notifier).loadMore(),
        ),
      );
    }

    if (!page.loadingMore) {
      // After the frame: a provider must not change while widgets build.
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (mounted) ref.read(wordsProvider(_query).notifier).loadMore();
      });
    }

    return const Padding(
      padding: EdgeInsets.symmetric(vertical: AppSpacing.md),
      child: Center(child: CircularProgressIndicator()),
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
