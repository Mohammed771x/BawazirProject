import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../app/router.dart';
import '../../core/api/api_providers.dart';
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
/// Nothing is hidden: archived words are still listed, because a word is never
/// deleted (rule R8) and disappearing from this screen would look exactly like
/// deletion.
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
          return WordTile(
            word: word,
            onTap: () => context.push(Routes.word(word.id)),
          );
        },
      ),
    );
  }
}
