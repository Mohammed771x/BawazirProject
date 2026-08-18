import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../app/router.dart';
import '../../core/l10n/app_strings.dart';
import '../../core/storage/preferences_providers.dart';
import '../../core/theme/app_tokens.dart';
import '../../core/widgets/brand.dart';

/// The first thing a new learner sees.
///
/// Four short screens that answer one question — *what is this and why should I
/// bother?* — before asking for anything. Authentication comes after the
/// explanation, not before it.
///
/// Seen once. Completing it (or skipping) records the fact on the device, and
/// the router never shows it again: onboarding is about explaining the product,
/// and a returning learner already knows.
class WelcomeOnboardingScreen extends ConsumerStatefulWidget {
  const WelcomeOnboardingScreen({super.key});

  @override
  ConsumerState<WelcomeOnboardingScreen> createState() =>
      _WelcomeOnboardingScreenState();
}

class _WelcomeOnboardingScreenState
    extends ConsumerState<WelcomeOnboardingScreen> {
  final _controller = PageController();
  int _page = 0;

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  Future<void> _finish({required String destination}) async {
    // Recorded before navigating: if the learner backgrounds the app on the
    // auth screen and comes back, they should land on auth, not on slide one.
    await ref.read(appPreferencesProvider).setOnboardingSeen(true);
    if (mounted) context.go(destination);
  }

  @override
  Widget build(BuildContext context) {
    final s = ref.watch(stringsProvider);

    final pages = <_OnboardingPage>[
      _OnboardingPage(
        icon: Icons.auto_awesome_rounded,
        color: context.colors.primary,
        title: s.onboardingWelcomeTitle,
        body: s.onboardingWelcomeBody,
      ),
      _OnboardingPage(
        icon: Icons.bookmark_added_rounded,
        color: context.palette.skillReading,
        title: s.onboardingWordsTitle,
        body: s.onboardingWordsBody,
      ),
      _OnboardingPage(
        icon: Icons.record_voice_over_rounded,
        color: context.palette.skillSpeaking,
        title: s.onboardingSkillsTitle,
        body: s.onboardingSkillsBody,
      ),
      _OnboardingPage(
        icon: Icons.rocket_launch_rounded,
        color: context.palette.skillWriting,
        title: s.onboardingStartTitle,
        body: s.onboardingStartBody,
      ),
    ];

    final isLast = _page == pages.length - 1;

    return Scaffold(
      body: SafeArea(
        child: Padding(
          padding: const EdgeInsets.all(AppSpacing.lg),
          child: Column(
            children: [
              Row(
                children: [
                  // Left in every language — the brand is an identity, not a
                  // paragraph that follows the reading direction.
                  const Expanded(child: WordOsBrand(size: WordOsBrandSize.compact)),
                  if (!isLast)
                    TextButton(
                      onPressed: () => _finish(destination: Routes.register),
                      child: Text(s.skip),
                    ),
                ],
              ),
              Expanded(
                child: PageView.builder(
                  controller: _controller,
                  itemCount: pages.length,
                  onPageChanged: (page) => setState(() => _page = page),
                  itemBuilder: (context, index) => pages[index],
                ),
              ),
              _PageDots(count: pages.length, active: _page),
              const SizedBox(height: AppSpacing.lg),
              if (isLast) ...[
                FilledButton(
                  onPressed: () => _finish(destination: Routes.register),
                  child: Text(s.onboardingCreateAccount),
                ),
                const SizedBox(height: AppSpacing.xs),
                TextButton(
                  onPressed: () => _finish(destination: Routes.login),
                  child: Text(s.onboardingHaveAccount),
                ),
              ] else
                FilledButton(
                  onPressed: () => _controller.nextPage(
                    duration: const Duration(milliseconds: 280),
                    curve: Curves.easeOut,
                  ),
                  child: Text(s.next),
                ),
            ],
          ),
        ),
      ),
    );
  }
}

class _OnboardingPage extends StatelessWidget {
  const _OnboardingPage({
    required this.icon,
    required this.color,
    required this.title,
    required this.body,
  });

  final IconData icon;
  final Color color;
  final String title;
  final String body;

  @override
  Widget build(BuildContext context) {
    return Column(
      mainAxisAlignment: MainAxisAlignment.center,
      children: [
        Container(
          width: 128,
          height: 128,
          decoration: BoxDecoration(
            color: color.withValues(alpha: 0.12),
            shape: BoxShape.circle,
          ),
          child: Icon(icon, size: 56, color: color),
        ),
        const SizedBox(height: AppSpacing.xl),
        Text(
          title,
          textAlign: TextAlign.center,
          style: context.text.headlineSmall,
        ),
        const SizedBox(height: AppSpacing.sm),
        ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 380),
          child: Text(
            body,
            textAlign: TextAlign.center,
            style: context.text.bodyLarge?.copyWith(
              height: 1.5,
              color: context.colors.onSurface.withValues(alpha: 0.7),
            ),
          ),
        ),
      ],
    );
  }
}

class _PageDots extends StatelessWidget {
  const _PageDots({required this.count, required this.active});

  final int count;
  final int active;

  @override
  Widget build(BuildContext context) {
    return Row(
      mainAxisAlignment: MainAxisAlignment.center,
      children: [
        for (var i = 0; i < count; i++)
          AnimatedContainer(
            duration: const Duration(milliseconds: 220),
            margin: const EdgeInsets.symmetric(horizontal: 4),
            width: i == active ? 22 : 8,
            height: 8,
            decoration: BoxDecoration(
              color: i == active
                  ? context.colors.primary
                  : context.palette.border,
              borderRadius: BorderRadius.circular(4),
            ),
          ),
      ],
    );
  }
}
