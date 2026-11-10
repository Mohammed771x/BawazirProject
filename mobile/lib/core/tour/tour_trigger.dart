import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../l10n/app_strings.dart';
import '../storage/preferences_providers.dart';
import 'tour.dart';

/// Starts a screen's tour the first time the screen is really in front of the
/// learner (ADR-128).
///
/// "Really in front" is the whole job. The hub, My Words and Settings are
/// built side by side in the shell's tab stack, and a tab that is merely built
/// must not start explaining itself from behind another one; nor may a screen
/// with a dialog or a pushed page over it. So the trigger waits for its tab to
/// be the active one and its route to be the top one, and only then — after a
/// short pause for the screen's own transition to settle — asks [AppTour].
class TourTrigger extends ConsumerStatefulWidget {
  const TourTrigger({
    super.key,
    required this.tours,
    required this.child,
    this.enabled = true,
  });

  /// Built when the tour starts, in the learner's current language and theme.
  final List<Tour> Function(BuildContext context, AppStrings s) tours;

  /// False until there is something to point at — data still loading.
  final bool enabled;

  final Widget child;

  @override
  ConsumerState<TourTrigger> createState() => _TourTriggerState();
}

class _TourTriggerState extends ConsumerState<TourTrigger> {
  /// Long enough for a page transition to finish, so the spotlight is measured
  /// where the element ends up rather than where it slid in from.
  static const _settle = Duration(milliseconds: 550);
  static const _retry = Duration(milliseconds: 900);

  Timer? _timer;

  /// Counted in [AppTour.whenIdle] while a tour is scheduled here.
  bool _counted = false;

  void _count(bool on) {
    if (on == _counted) return;
    _counted = on;
    on ? AppTour.waitingStarted() : AppTour.waitingEnded();
  }

  @override
  void didUpdateWidget(TourTrigger old) {
    super.didUpdateWidget(old);
    if (widget.enabled && !old.enabled) _schedule(_settle);
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    // Runs once after the widget is inserted, and again whenever its tab
    // becomes the active one — reading TickerMode is what subscribes to that.
    // (Not from initState: the tours read the theme, which cannot be looked
    // up before this point.)
    if (TickerMode.valuesOf(context).enabled && _timer == null) {
      _schedule(_settle);
    }
  }

  @override
  void dispose() {
    _timer?.cancel();
    _count(false);
    super.dispose();
  }

  bool get _pending {
    if (!widget.enabled) return false;
    final prefs = ref.read(appPreferencesProvider);
    return AppTour.unseen(prefs, _build()).isNotEmpty;
  }

  List<Tour> _build() => widget.tours(context, ref.read(stringsProvider));

  void _schedule(Duration after) {
    _timer?.cancel();
    _timer = null;
    // Checked before a timer exists: a screen whose tours are all seen — every
    // widget test — must leave nothing pending behind it.
    if (!_pending) return _count(false);
    _count(true);
    _timer = Timer(after, _attempt);
  }

  Future<void> _attempt() async {
    _timer = null;
    if (!mounted || !_pending) return _count(false);

    // Another tab: nothing to retry, becoming active calls back in through
    // didChangeDependencies.
    if (!TickerMode.valuesOf(context).enabled) return _count(false);

    // Covered, or another tour is running: look again shortly.
    if (AppTour.running || !_onTop(context)) {
      _timer = Timer(_retry, _attempt);
      return;
    }

    // Handed over: from here AppTour.running is what holds others back.
    final start =
        AppTour.start(context, ref.read(appPreferencesProvider), _build());
    _count(false);
    await start;
  }

  /// Whether every route between this widget and the root navigator is the
  /// top one of its own navigator — a tab inside the shell, and the shell
  /// itself.
  static bool _onTop(BuildContext context) {
    BuildContext? at = context;
    while (at != null && at.mounted) {
      final route = ModalRoute.of(at);
      if (route == null) return true;
      if (!route.isCurrent) return false;
      at = route.navigator?.context;
    }
    return true;
  }

  @override
  Widget build(BuildContext context) {
    // "Replay the tour" in Settings: offer it again where it already stands.
    ref.listen(tourEpochProvider, (_, _) => _schedule(_settle));
    return widget.child;
  }
}
