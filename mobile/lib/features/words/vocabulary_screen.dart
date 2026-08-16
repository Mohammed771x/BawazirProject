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

final wordsProvider = FutureProvider.autoDispose.family<WordPage, WordState>((
  ref,
  state,
) {
  return ref.watch(wordOsApiProvider).words(state: state);
});

class VocabularyScreen extends ConsumerStatefulWidget {
  const VocabularyScreen({super.key});

  @override
  ConsumerState<VocabularyScreen> createState() => _VocabularyScreenState();
}

class _VocabularyScreenState extends ConsumerState<VocabularyScreen>
    with SingleTickerProviderStateMixin {
  late final TabController _tabs = TabController(length: 3, vsync: this);

  @override
  void dispose() {
    _tabs.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final s = ref.watch(stringsProvider);

    return Scaffold(
      appBar: AppBar(
        title: Text(s.vocabulary),
        bottom: TabBar(
          controller: _tabs,
          dividerColor: context.palette.border,
          tabs: [
            Tab(text: s.learning),
            Tab(text: s.active),
            Tab(text: s.archived),
          ],
        ),
      ),
      body: TabBarView(
        controller: _tabs,
        children: const [
          _WordList(state: WordState.learning),
          _WordList(state: WordState.active),
          _WordList(state: WordState.archived),
        ],
      ),
    );
  }
}

class _WordList extends ConsumerWidget {
  const _WordList({required this.state});

  final WordState state;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    final words = ref.watch(wordsProvider(state));

    return words.when(
      loading: () => BusyView(message: s.loading),
      error: (e, _) => ErrorView(
        message: s.somethingWentWrong,
        retryLabel: s.retry,
        onRetry: () => ref.invalidate(wordsProvider(state)),
      ),
      data: (page) {
        if (page.items.isEmpty) {
          return EmptyState(
            icon: Icons.inbox_rounded,
            title: s.noWordsYet,
            message: state == WordState.learning
                ? s.chooseMeaningSubtitle
                : null,
          );
        }
        return RefreshIndicator(
          onRefresh: () async => ref.refresh(wordsProvider(state).future),
          child: ListView.separated(
            padding: const EdgeInsets.fromLTRB(
              AppSpacing.md,
              AppSpacing.md,
              AppSpacing.md,
              // Clear the floating "Add word" button and the nav bar.
              AppSpacing.xxl * 2,
            ),
            itemCount: page.items.length,
            separatorBuilder: (_, _) => const SizedBox(height: AppSpacing.xs),
            itemBuilder: (context, index) {
              final word = page.items[index];
              return WordTile(
                word: word,
                onTap: () => context.push(Routes.word(word.id)),
              );
            },
          ),
        );
      },
    );
  }
}
