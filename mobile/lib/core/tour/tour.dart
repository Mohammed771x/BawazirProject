import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../storage/app_preferences.dart';
import 'tour_overlay.dart';

/// The guided tour (ADR-128): the screen goes dark, the one thing being
/// explained stays lit inside a ring, and a short card beside it says what it
/// is for. No mascot and no animation beyond the spotlight moving — the tour
/// is there to point, and gets out of the way.
///
/// Each screen hands [AppTour.start] its [Tour]s; the engine is one. A tour is
/// shown **once per installation**, and Settings can show them all again.

/// The shape of the lit area around the thing being explained.
enum TourShape {
  /// A circle — for a button or an icon.
  circle,

  /// A rounded rectangle — for a card, a field or a list.
  rect,
}

class TourStep {
  const TourStep({
    this.anchor,
    this.altAnchor,
    required this.title,
    required this.body,
    this.icon,
    this.color,
    this.shape = TourShape.rect,
    this.padding = 8,
    this.optional = false,
  });

  /// The [TourAnchor] to light. Null is a step in the middle of the screen,
  /// with nothing lit — a welcome, or a closing word.
  final String? anchor;

  /// Lit instead when [anchor] is not on screen — the typing box a device
  /// with no microphone shows in place of the microphone.
  final String? altAnchor;

  final String title;
  final String body;

  /// Shown at the top of the card, in [color].
  final IconData? icon;
  final Color? color;
  final TourShape shape;

  /// How far the lit area reaches past the element.
  final double padding;

  /// The element may legitimately not exist — a card with nothing to show, a
  /// list with no words in it yet. The step is then dropped rather than
  /// pointing at nothing. A required step whose element is missing is shown
  /// in the middle of the screen instead, because what it says still holds.
  final bool optional;
}

/// A named run of steps, remembered as seen on its own.
///
/// One screen can offer several: the first placement question carries the
/// basics *and* how this kind of question is answered, and the next question
/// of a different kind only the latter.
class Tour {
  const Tour(this.id, this.steps);

  final String id;
  final List<TourStep> steps;
}

/// Bumped by "Replay the tour" in Settings, so every [TourTrigger] that is
/// already built offers its tour again rather than waiting to be rebuilt.
final tourEpochProvider = StateProvider<int>((ref) => 0);

class AppTour {
  AppTour._();

  static bool _running = false;

  /// One tour at a time: two screens that both want to explain themselves
  /// must not stack two dark layers.
  static bool get running => _running;

  /// Screens whose tour is about to start — scheduled, not yet showing.
  static int _waiting = 0;

  static void waitingStarted() => _waiting++;
  static void waitingEnded() => _waiting = _waiting > 0 ? _waiting - 1 : 0;

  /// Completes once no tour is showing or about to show.
  ///
  /// For anything that raises a system dialog — the microphone permission
  /// above all. Asked while a tour was up, the two landed on top of each
  /// other, the learner took longer than the recogniser's ten seconds to get
  /// through both, and the microphone was written off for the whole run.
  ///
  /// A tour being read is waited out however long it takes; one that is only
  /// *about* to start is given a few seconds, so a trigger that never fires
  /// cannot keep the microphone shut.
  static Future<void> whenIdle() async {
    final giveUpWaiting = DateTime.now().add(const Duration(seconds: 4));
    while (_running ||
        (_waiting > 0 && DateTime.now().isBefore(giveUpWaiting))) {
      await Future<void>.delayed(const Duration(milliseconds: 250));
    }
  }

  /// The tours among [tours] this installation has not seen.
  static List<Tour> unseen(AppPreferences prefs, List<Tour> tours) => [
    for (final t in tours)
      if (!prefs.tourSeen(t.id)) t,
  ];

  /// Shows whatever of [tours] has not been seen, as one run.
  static Future<void> start(
    BuildContext context,
    AppPreferences prefs,
    List<Tour> tours,
  ) async {
    if (_running) return;
    final pending = unseen(prefs, tours);
    final steps = [for (final t in pending) ...t.steps];
    if (steps.isEmpty) return;

    _running = true;
    try {
      for (final t in pending) {
        await prefs.markTourSeen(t.id);
      }
      if (!context.mounted) return;
      // An open keyboard covers half of what is being pointed at.
      FocusManager.instance.primaryFocus?.unfocus();
      await Navigator.of(context, rootNavigator: true).push(
        PageRouteBuilder<void>(
          opaque: false,
          transitionDuration: Duration.zero,
          reverseTransitionDuration: Duration.zero,
          pageBuilder: (_, _, _) => TourOverlay(steps: steps),
        ),
      );
    } finally {
      _running = false;
    }
  }
}

/// Where each [TourAnchor] currently is.
///
/// A registry by name rather than keys handed down, because the things a tour
/// visits are spread over widgets that do not know each other — the bottom
/// bar lives in the shell, the cards in the hub. One name may be built more
/// than once (a screen pushed twice); the newest wins, being the visible one.
class TourAnchors {
  TourAnchors._();

  static final Map<String, List<GlobalKey>> _live = {};

  static BuildContext? contextOf(String id) {
    final keys = _live[id];
    if (keys == null) return null;
    for (final k in keys.reversed) {
      final ctx = k.currentContext;
      if (ctx != null) return ctx;
    }
    return null;
  }

  /// The element's rectangle in screen coordinates, or null when it is not
  /// laid out — or laid out with no size, which is the same as absent.
  static Rect? rectOf(String id) {
    final box = contextOf(id)?.findRenderObject();
    if (box is! RenderBox || !box.attached || !box.hasSize) return null;
    if (box.size.isEmpty) return null;
    return box.localToGlobal(Offset.zero) & box.size;
  }

  static void _add(String id, GlobalKey k) => (_live[id] ??= []).add(k);

  static void _remove(String id, GlobalKey k) {
    _live[id]?.remove(k);
    if (_live[id]?.isEmpty ?? false) _live.remove(id);
  }
}

/// Marks an element a tour can point at. Changes nothing about how it draws.
class TourAnchor extends StatefulWidget {
  const TourAnchor({super.key, required this.id, required this.child});

  final String id;
  final Widget child;

  @override
  State<TourAnchor> createState() => _TourAnchorState();
}

class _TourAnchorState extends State<TourAnchor> {
  late final GlobalKey _key = GlobalKey(debugLabel: 'tour:${widget.id}');

  @override
  void initState() {
    super.initState();
    TourAnchors._add(widget.id, _key);
  }

  @override
  void didUpdateWidget(TourAnchor old) {
    super.didUpdateWidget(old);
    if (old.id != widget.id) {
      TourAnchors._remove(old.id, _key);
      TourAnchors._add(widget.id, _key);
    }
  }

  @override
  void dispose() {
    TourAnchors._remove(widget.id, _key);
    super.dispose();
  }

  @override
  Widget build(BuildContext context) =>
      KeyedSubtree(key: _key, child: widget.child);
}
