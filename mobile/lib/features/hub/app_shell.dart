import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../app/router.dart';
import '../../core/l10n/app_strings.dart';
import '../../core/theme/app_tokens.dart';
import '../../core/tour/tour.dart';
import '../tour/app_tours.dart';

/// Bottom-navigation shell: Skills Hub · Vocabulary · Settings, with Add Word
/// always one tap away.
class AppShell extends ConsumerWidget {
  const AppShell({super.key, required this.shell});

  final StatefulNavigationShell shell;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);

    return Scaffold(
      body: shell,
      floatingActionButton: shell.currentIndex == 2
          ? null
          : TourAnchor(
              id: AppTours.hubAdd,
              child: FloatingActionButton.extended(
                onPressed: () => context.push(Routes.addWord),
                icon: const Icon(Icons.add_rounded),
                label: Text(s.addWord),
              ),
            ),
      bottomNavigationBar: Container(
        decoration: BoxDecoration(
          border: Border(top: BorderSide(color: context.palette.border)),
        ),
        child: NavigationBar(
          selectedIndex: shell.currentIndex,
          onDestinationSelected: (index) => shell.goBranch(
            index,
            initialLocation: index == shell.currentIndex,
          ),
          destinations: [
            NavigationDestination(
              icon: const Icon(Icons.grid_view_outlined),
              selectedIcon: const Icon(Icons.grid_view_rounded),
              label: s.skillsHub,
            ),
            // The icons carry the tour's anchors (ADR-128): the bar gives
            // no handle on a destination as a whole.
            NavigationDestination(
              icon: const TourAnchor(
                id: AppTours.navWords,
                child: Icon(Icons.style_outlined),
              ),
              selectedIcon: const TourAnchor(
                id: AppTours.navWords,
                child: Icon(Icons.style_rounded),
              ),
              label: s.myWords,
            ),
            NavigationDestination(
              icon: const TourAnchor(
                id: AppTours.navSettings,
                child: Icon(Icons.settings_outlined),
              ),
              selectedIcon: const TourAnchor(
                id: AppTours.navSettings,
                child: Icon(Icons.settings_rounded),
              ),
              label: s.settings,
            ),
          ],
        ),
      ),
    );
  }
}
