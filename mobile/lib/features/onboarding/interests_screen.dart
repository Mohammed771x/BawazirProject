import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_providers.dart';
import '../../core/api/wordos_api.dart';
import '../../core/l10n/app_strings.dart';
import '../../core/models/models.dart';
import '../../core/theme/app_tokens.dart';
import '../../core/widgets/app_widgets.dart';
import '../auth/session_controller.dart';
import 'interests_editor.dart';

final interestOptionsProvider = FutureProvider<List<InterestOption>>(
  (ref) => ref.watch(wordOsApiProvider).interestOptions(),
);

class InterestsScreen extends ConsumerStatefulWidget {
  const InterestsScreen({super.key});

  @override
  ConsumerState<InterestsScreen> createState() => _InterestsScreenState();
}

class _InterestsScreenState extends ConsumerState<InterestsScreen> {
  Set<String> _selected = {};
  bool _saving = false;

  Future<void> _continue() async {
    setState(() => _saving = true);
    try {
      final profile =
          await ref.read(wordOsApiProvider).saveInterests(_selected.toList());
      ref.read(sessionProvider.notifier).updateUser(profile);
    } on ApiException catch (e) {
      if (mounted) {
        ScaffoldMessenger.of(context)
            .showSnackBar(SnackBar(content: Text(e.message)));
      }
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final s = ref.watch(stringsProvider);
    final options = ref.watch(interestOptionsProvider);

    return Scaffold(
      body: SafeArea(
        child: options.when(
          loading: () => BusyView(message: s.loading),
          error: (e, _) => ErrorView(
            message: s.somethingWentWrong,
            retryLabel: s.retry,
            onRetry: () => ref.invalidate(interestOptionsProvider),
          ),
          data: (items) => Column(
            children: [
              Expanded(
                child: SingleChildScrollView(
                  padding: const EdgeInsets.all(AppSpacing.md),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      const SizedBox(height: AppSpacing.md),
                      Text(s.interestsTitle, style: context.text.headlineMedium),
                      const SizedBox(height: AppSpacing.xs),
                      Text(
                        s.interestsSubtitle,
                        style: context.text.bodyMedium?.copyWith(
                          color:
                              context.colors.onSurface.withValues(alpha: 0.65),
                        ),
                      ),
                      const SizedBox(height: AppSpacing.sm),
                      _SelectionHint(count: _selected.length),
                      const SizedBox(height: AppSpacing.lg),
                      InterestsEditor(
                        options: items,
                        selected: _selected,
                        enabled: !_saving,
                        onChanged: (next) => setState(() => _selected = next),
                      ),
                    ],
                  ),
                ),
              ),
              Padding(
                padding: const EdgeInsets.all(AppSpacing.md),
                child: FilledButton(
                  onPressed: _selected.isEmpty || _saving ? null : _continue,
                  child: _saving
                      ? const SizedBox(
                          width: 20,
                          height: 20,
                          child: CircularProgressIndicator(
                            strokeWidth: 2.4,
                            color: Colors.white,
                          ),
                        )
                      : Text(s.continueLabel),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// Nudges the learner toward picking many interests — the richer this list is,
/// the more personal the generated content can be (demo review §3).
class _SelectionHint extends ConsumerWidget {
  const _SelectionHint({required this.count});

  final int count;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    final enough = count >= 3;
    final color = enough ? context.palette.success : context.colors.primary;

    return Row(
      children: [
        Icon(
          enough ? Icons.check_circle_rounded : Icons.auto_awesome_rounded,
          size: 18,
          color: color,
        ),
        const SizedBox(width: AppSpacing.xs),
        Expanded(
          child: Text(
            enough ? s.interestsSelectedCount(count) : s.interestsPickMany,
            style: context.text.bodySmall?.copyWith(color: color),
          ),
        ),
      ],
    );
  }
}
