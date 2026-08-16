import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../core/models/enums.dart';
import '../features/auth/login_screen.dart';
import '../features/auth/register_screen.dart';
import '../features/auth/session_controller.dart';
import '../features/developer/developer_screen.dart';
import '../features/developer/developer_user_screen.dart';
import '../features/hub/app_shell.dart';
import '../features/hub/hub_screen.dart';
import '../features/onboarding/interests_screen.dart';
import '../features/onboarding/placement_screen.dart';
import '../features/review/weekly_review_screen.dart';
import '../features/session/session_screen.dart';
import '../features/settings/settings_screen.dart';
import '../features/words/add_word_screen.dart';
import '../features/words/vocabulary_screen.dart';
import '../features/words/word_detail_screen.dart';

class Routes {
  const Routes._();

  static const splash = '/splash';
  static const login = '/login';
  static const register = '/register';
  static const interests = '/interests';
  static const placement = '/placement';
  static const hub = '/hub';
  static const vocabulary = '/vocabulary';
  static const settings = '/settings';
  static const addWord = '/add-word';
  static const weeklyReview = '/weekly-review';
  static const developer = '/developer';

  static String developerUser(String id) => '/developer/users/$id';

  static String word(String id) => '/word/$id';
  static String session(SkillType skill) =>
      '/session/${skill.wire.toLowerCase()}';
}

final _rootKey = GlobalKey<NavigatorState>(debugLabel: 'root');

/// Routing with onboarding guards. The guard follows the *server-provided*
/// `onboardingStage` — the client never decides where the user "should" be.
final routerProvider = Provider<GoRouter>((ref) {
  final refresh = ValueNotifier<int>(0);
  ref.listen(sessionProvider, (_, _) => refresh.value++);
  ref.onDispose(refresh.dispose);

  return GoRouter(
    navigatorKey: _rootKey,
    initialLocation: Routes.splash,
    refreshListenable: refresh,
    redirect: (context, state) {
      final session = ref.read(sessionProvider);
      final path = state.matchedLocation;

      if (session.restoring) {
        return path == Routes.splash ? null : Routes.splash;
      }

      final isAuthRoute = path == Routes.login || path == Routes.register;

      if (!session.isSignedIn) {
        return isAuthRoute ? null : Routes.login;
      }

      // The Owner area is a separate space, not a section of Settings. This
      // guard is UX, not security — the API refuses these calls for a
      // non-owner regardless of what the client renders (`MockAdmin`).
      if (path.startsWith(Routes.developer) &&
          session.user!.role != UserRole.owner) {
        return Routes.hub;
      }

      switch (session.user!.onboardingStage) {
        case OnboardingStage.interests:
          return path == Routes.interests ? null : Routes.interests;
        case OnboardingStage.placement:
          return path == Routes.placement ? null : Routes.placement;
        case OnboardingStage.complete:
          if (isAuthRoute ||
              path == Routes.splash ||
              path == Routes.interests ||
              path == Routes.placement) {
            return Routes.hub;
          }
          return null;
      }
    },
    routes: [
      GoRoute(
        path: Routes.splash,
        builder: (_, _) => const _SplashScreen(),
      ),
      GoRoute(path: Routes.login, builder: (_, _) => const LoginScreen()),
      GoRoute(path: Routes.register, builder: (_, _) => const RegisterScreen()),
      GoRoute(
        path: Routes.interests,
        builder: (_, _) => const InterestsScreen(),
      ),
      GoRoute(
        path: Routes.placement,
        builder: (_, _) => const PlacementScreen(),
      ),
      StatefulShellRoute.indexedStack(
        builder: (_, _, shell) => AppShell(shell: shell),
        branches: [
          StatefulShellBranch(
            routes: [
              GoRoute(path: Routes.hub, builder: (_, _) => const HubScreen()),
            ],
          ),
          StatefulShellBranch(
            routes: [
              GoRoute(
                path: Routes.vocabulary,
                builder: (_, _) => const VocabularyScreen(),
              ),
            ],
          ),
          StatefulShellBranch(
            routes: [
              GoRoute(
                path: Routes.settings,
                builder: (_, _) => const SettingsScreen(),
              ),
            ],
          ),
        ],
      ),
      GoRoute(
        path: Routes.addWord,
        parentNavigatorKey: _rootKey,
        builder: (_, _) => const AddWordScreen(),
      ),
      GoRoute(
        path: Routes.weeklyReview,
        parentNavigatorKey: _rootKey,
        builder: (_, _) => const WeeklyReviewScreen(),
      ),
      GoRoute(
        path: Routes.developer,
        parentNavigatorKey: _rootKey,
        builder: (_, _) => const DeveloperScreen(),
        routes: [
          GoRoute(
            path: 'users/:id',
            parentNavigatorKey: _rootKey,
            builder: (_, state) =>
                DeveloperUserScreen(userId: state.pathParameters['id']!),
          ),
        ],
      ),
      GoRoute(
        path: '/word/:id',
        parentNavigatorKey: _rootKey,
        builder: (_, state) =>
            WordDetailScreen(wordId: state.pathParameters['id']!),
      ),
      GoRoute(
        path: '/session/:skill',
        parentNavigatorKey: _rootKey,
        builder: (_, state) => SessionScreen(
          skill: SkillType.fromWire(
            state.pathParameters['skill']!.toUpperCase(),
          ),
        ),
      ),
    ],
  );
});

class _SplashScreen extends StatelessWidget {
  const _SplashScreen();

  @override
  Widget build(BuildContext context) {
    return const Scaffold(
      body: Center(child: CircularProgressIndicator()),
    );
  }
}
