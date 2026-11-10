import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../l10n/app_strings.dart';
import '../theme/app_tokens.dart';
import 'tour.dart';

/// The tour's stage: a dark layer with one lit opening, a ring around it, and
/// a card beside it (ADR-128).
///
/// Moving between steps is three beats — the card fades out, the opening
/// glides to the next element, the new card fades in beside it — so the eye
/// is carried from one place to the next instead of hunting for where the
/// light went. A tap anywhere, or "Next", moves on; "Skip" or the back
/// gesture ends it.
class TourOverlay extends ConsumerStatefulWidget {
  const TourOverlay({super.key, required this.steps});

  final List<TourStep> steps;

  @override
  ConsumerState<TourOverlay> createState() => _TourOverlayState();
}

/// What is computed for a step: the opening, and the band the card sits in.
class _Frame {
  const _Frame({required this.hole, required this.region, required this.below});

  /// Empty (zero-size, centred) for a step with nothing lit.
  final RRect hole;

  /// Where the card may go — the larger free band above or below the opening,
  /// or the whole safe area for a centred step.
  final Rect region;

  /// The card sits under the opening (and points up at it).
  final bool below;

  bool get centred => hole.isEmpty;
}

class _TourOverlayState extends ConsumerState<TourOverlay>
    with TickerProviderStateMixin {
  late final AnimationController _scrim = AnimationController(
    vsync: this,
    duration: const Duration(milliseconds: 280),
  );
  late final AnimationController _move = AnimationController(
    vsync: this,
    duration: const Duration(milliseconds: 460),
  );
  late final AnimationController _card = AnimationController(
    vsync: this,
    duration: const Duration(milliseconds: 260),
  );
  late final AnimationController _pulse = AnimationController(
    vsync: this,
    duration: const Duration(milliseconds: 1600),
  );

  late List<TourStep> _steps = widget.steps;
  int _index = 0;
  RRect? _fromHole;
  _Frame? _frame;
  bool _busy = true;
  bool _still = false;

  TourStep get _step => _steps[_index];

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) => _begin());
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _still = MediaQuery.disableAnimationsOf(context);
  }

  @override
  void dispose() {
    _scrim.dispose();
    _move.dispose();
    _card.dispose();
    _pulse.dispose();
    super.dispose();
  }

  // ─────────────────────────── sequence ───────────────────────────

  Future<void> _begin() async {
    // Steps for elements that are not there are dropped before the first one
    // shows, so the dots count what will actually be seen.
    _steps = [
      for (final s in _steps)
        if (!s.optional || _present(s.anchor) || _present(s.altAnchor)) s,
    ];
    if (_steps.isEmpty) return _pop();

    await _reveal(0);
    if (!mounted) return;
    _fromHole = _frame!.hole;
    _move.value = 1;
    if (!_still) _pulse.repeat();
    await _run(_scrim, forward: true);
    await _run(_card, forward: true);
    _busy = false;
  }

  /// Scrolls step [i]'s element into view and measures it.
  Future<void> _reveal(int i) async {
    final step = _steps[i];
    final id = _present(step.anchor)
        ? step.anchor
        : _present(step.altAnchor)
        ? step.altAnchor
        : null;

    final ctx = id == null ? null : TourAnchors.contextOf(id);
    if (ctx != null && ctx.mounted) {
      await Scrollable.ensureVisible(
        ctx,
        alignment: 0.3,
        duration: _still ? Duration.zero : const Duration(milliseconds: 380),
        curve: Curves.easeInOutCubic,
      );
      // The scroll lands on this frame; the position is right on the next.
      await WidgetsBinding.instance.endOfFrame;
    }
    if (!mounted) return;
    setState(() {
      _index = i;
      _frame = _frameFor(step, id);
    });
  }

  Future<void> _next() async {
    if (_busy) return;
    if (_index >= _steps.length - 1) return _close();
    _busy = true;
    HapticFeedback.selectionClick();

    await _run(_card, forward: false);
    if (!mounted) return;
    // Pinned where it is before the next step is measured, so the opening
    // glides there instead of jumping on the frame the new step lands.
    _fromHole = _hole;
    _move.value = 0;
    await _reveal(_index + 1);
    if (!mounted) return;
    await _run(_move, forward: true);
    await _run(_card, forward: true);
    _busy = false;
  }

  Future<void> _close() async {
    if (!mounted) return;
    _busy = true;
    await _run(_card, forward: false);
    await _run(_scrim, forward: false);
    _scrollBack();
    _pop();
  }

  void _pop() {
    if (mounted) Navigator.of(context).pop();
  }

  /// Returns the screen to its top. The tour scrolled it to show each part,
  /// and leaving the learner half-way down a list they never scrolled is the
  /// tour having moved their things.
  void _scrollBack() {
    for (final s in _steps) {
      final ctx = s.anchor == null ? null : TourAnchors.contextOf(s.anchor!);
      if (ctx == null || !ctx.mounted) continue;
      final position = Scrollable.maybeOf(ctx)?.position;
      if (position != null && position.pixels != position.minScrollExtent) {
        position.animateTo(
          position.minScrollExtent,
          duration: const Duration(milliseconds: 360),
          curve: Curves.easeOutCubic,
        );
      }
      return;
    }
  }

  Future<void> _run(AnimationController c, {required bool forward}) async {
    if (_still) {
      c.value = forward ? 1 : 0;
      return;
    }
    try {
      await (forward ? c.forward() : c.reverse()).orCancel;
    } on TickerCanceled {
      // Disposed mid-animation: nothing left to animate.
    }
  }

  static bool _present(String? id) =>
      id != null && TourAnchors.rectOf(id) != null;

  // ─────────────────────────── geometry ───────────────────────────

  static const double _gap = 14;

  /// Room the card needs beside the opening. Below this the opening is
  /// trimmed from its far end rather than the card being squeezed: the head of
  /// a long element — its title, its first row — is what identifies it.
  static const double _cardRoom = 230;

  _Frame _frameFor(TourStep step, String? id) {
    final mq = MediaQuery.of(context);
    final size = mq.size;
    final safe = Rect.fromLTRB(
      AppSpacing.md,
      mq.padding.top + AppSpacing.xs,
      size.width - AppSpacing.md,
      size.height -
          math.max(mq.padding.bottom, mq.viewInsets.bottom) -
          AppSpacing.xs,
    );

    final rect = id == null ? null : TourAnchors.rectOf(id);
    if (rect == null) {
      return _Frame(
        hole: RRect.fromRectAndRadius(
          Rect.fromCenter(center: safe.center, width: 0, height: 0),
          Radius.zero,
        ),
        region: safe,
        below: true,
      );
    }

    // A "circle" around something much wider than tall — the typing box that
    // stands in for a microphone — would light half the screen; it becomes a
    // rounded rectangle instead.
    final round =
        step.shape == TourShape.circle &&
        math.max(rect.width, rect.height) <
            math.min(rect.width, rect.height) * 1.8;

    final screen = Offset.zero & size;
    Rect area;
    double radius;
    if (round) {
      final d = math.max(rect.width, rect.height) + step.padding * 2;
      area = Rect.fromCenter(center: rect.center, width: d, height: d);
      radius = d / 2;
    } else {
      area = rect.inflate(step.padding).intersect(screen);
      radius = math.min(20, area.shortestSide / 2);
    }

    var above = area.top - _gap - safe.top;
    var below = safe.bottom - area.bottom - _gap;
    if (!round && math.max(above, below) < _cardRoom) {
      final cut = safe.bottom - _gap - _cardRoom;
      if (cut > area.top + 72) {
        area = Rect.fromLTRB(area.left, area.top, area.right, cut);
      } else {
        // Its head is at the bottom of the screen: keep the bottom, card above.
        final top = math.max(area.top, safe.top + _gap + _cardRoom);
        area = Rect.fromLTRB(area.left, top, area.right, area.bottom);
      }
      above = area.top - _gap - safe.top;
      below = safe.bottom - area.bottom - _gap;
    }

    final useBelow = below >= above;
    return _Frame(
      hole: RRect.fromRectAndRadius(area, Radius.circular(radius)),
      region: useBelow
          ? Rect.fromLTRB(
              safe.left,
              area.bottom + _gap,
              safe.right,
              safe.bottom,
            )
          : Rect.fromLTRB(safe.left, safe.top, safe.right, area.top - _gap),
      below: useBelow,
    );
  }

  RRect get _hole {
    final to = _frame!.hole;
    final from = _fromHole ?? to;
    // From nothing to something (a centred step to a lit one) grows the
    // opening out of the element itself, rather than out of the screen's
    // middle where nothing is.
    final start = from.isEmpty
        ? RRect.fromRectAndRadius(
            Rect.fromCenter(center: to.center, width: 0, height: 0),
            Radius.zero,
          )
        : from;
    final end = to.isEmpty
        ? RRect.fromRectAndRadius(
            Rect.fromCenter(center: start.center, width: 0, height: 0),
            Radius.zero,
          )
        : to;
    final t = Curves.easeInOutCubic.transform(_move.value);
    return RRect.lerp(start, end, t)!;
  }

  // ─────────────────────────── drawing ───────────────────────────

  @override
  Widget build(BuildContext context) {
    final frame = _frame;
    final accent = _step.color ?? context.colors.primary;

    return PopScope(
      canPop: false,
      onPopInvokedWithResult: (didPop, _) {
        if (!didPop && !_busy) _close();
      },
      child: Material(
        type: MaterialType.transparency,
        child: frame == null
            // Measuring the first step: the layer swallows taps already.
            ? const SizedBox.expand()
            : AnimatedBuilder(
                animation: Listenable.merge([_scrim, _move, _card, _pulse]),
                builder: (context, _) {
                  final hole = _hole;
                  final fade = Curves.easeOut.transform(_scrim.value);
                  return Stack(
                    children: [
                      // Swallows every tap — the screen underneath is shown,
                      // not used — and turns it into "next".
                      Positioned.fill(
                        child: GestureDetector(
                          behavior: HitTestBehavior.opaque,
                          onTap: _next,
                          child: Opacity(
                            opacity: fade,
                            child: CustomPaint(
                              painter: _SpotlightPainter(
                                hole: hole,
                                accent: accent,
                                pulse: _still ? 0 : _pulse.value,
                                ringOpacity: _move.value,
                              ),
                            ),
                          ),
                        ),
                      ),
                      Positioned.fromRect(
                        rect: frame.region,
                        child: _cardSlot(frame, accent),
                      ),
                    ],
                  );
                },
              ),
      ),
    );
  }

  Widget _cardSlot(_Frame frame, Color accent) {
    final s = ref.watch(stringsProvider);
    final t = Curves.easeOutCubic.transform(_card.value);
    final width = math.min(frame.region.width, 400.0);
    final cardLeft = frame.region.left + (frame.region.width - width) / 2;
    // The pointer sits under (or over) the opening's centre, wherever the
    // card itself is.
    final pointerX = (frame.hole.center.dx - cardLeft).clamp(28.0, width - 28);

    final card = _TourCard(
      step: _step,
      accent: accent,
      index: _index,
      count: _steps.length,
      centred: frame.centred,
      pointerX: frame.centred ? null : pointerX,
      pointUp: frame.below,
      nextLabel: _index == _steps.length - 1 ? s.tourDone : s.next,
      skipLabel: s.tourSkip,
      onNext: _next,
      onSkip: _close,
    );

    return Align(
      alignment: frame.centred
          ? Alignment.center
          : frame.below
          ? Alignment.topCenter
          : Alignment.bottomCenter,
      child: Opacity(
        opacity: t,
        child: Transform.translate(
          // Slides in from the opening's side.
          offset: Offset(
            0,
            (1 - t) * (frame.centred ? 14 : (frame.below ? -10 : 10)),
          ),
          child: Transform.scale(
            scale: frame.centred ? 0.94 + 0.06 * t : 1,
            child: SizedBox(
              width: width,
              child: FittedBox(
                fit: BoxFit.scaleDown,
                alignment: frame.below
                    ? Alignment.topCenter
                    : Alignment.bottomCenter,
                child: SizedBox(width: width, child: card),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

/// The card: what this is, and what to do with it.
class _TourCard extends StatelessWidget {
  const _TourCard({
    required this.step,
    required this.accent,
    required this.index,
    required this.count,
    required this.centred,
    required this.pointerX,
    required this.pointUp,
    required this.nextLabel,
    required this.skipLabel,
    required this.onNext,
    required this.onSkip,
  });

  final TourStep step;
  final Color accent;
  final int index;
  final int count;
  final bool centred;

  /// Where along the card the pointer sits; null for no pointer.
  final double? pointerX;
  final bool pointUp;
  final String nextLabel;
  final String skipLabel;
  final VoidCallback onNext;
  final VoidCallback onSkip;

  static const double _pointer = 10;

  @override
  Widget build(BuildContext context) {
    final surface = context.colors.surface;
    final last = index == count - 1;

    final body = Container(
      padding: const EdgeInsets.fromLTRB(
        AppSpacing.md + 2,
        AppSpacing.md + 2,
        AppSpacing.md + 2,
        AppSpacing.sm,
      ),
      decoration: BoxDecoration(
        color: surface,
        borderRadius: const BorderRadius.all(AppRadii.lg),
        border: Border.all(color: accent.withValues(alpha: 0.28)),
        boxShadow: [
          BoxShadow(
            color: Colors.black.withValues(alpha: 0.28),
            blurRadius: 28,
            offset: const Offset(0, 10),
          ),
        ],
      ),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: centred
            ? CrossAxisAlignment.center
            : CrossAxisAlignment.start,
        children: [
          if (centred) ...[
            if (step.icon != null) _badge(context, size: 64, icon: 30),
            const SizedBox(height: AppSpacing.sm),
          ],
          if (!centred)
            Row(
              children: [
                if (step.icon != null) ...[
                  _badge(context, size: 34, icon: 18),
                  const SizedBox(width: AppSpacing.sm),
                ],
                Expanded(
                  child: Text(
                    step.title,
                    style: context.text.titleMedium?.copyWith(
                      fontWeight: FontWeight.w800,
                    ),
                  ),
                ),
              ],
            )
          else
            Text(
              step.title,
              textAlign: TextAlign.center,
              style: context.text.titleLarge?.copyWith(
                fontWeight: FontWeight.w800,
              ),
            ),
          const SizedBox(height: AppSpacing.xs),
          Text(
            step.body,
            textAlign: centred ? TextAlign.center : TextAlign.start,
            style: context.text.bodyMedium?.copyWith(
              height: 1.55,
              color: context.colors.onSurface.withValues(alpha: 0.78),
            ),
          ),
          const SizedBox(height: AppSpacing.sm),
          Row(
            children: [
              _Dots(index: index, count: count, color: accent),
              const Spacer(),
              if (!last)
                TextButton(
                  onPressed: onSkip,
                  style: TextButton.styleFrom(
                    foregroundColor: context.colors.onSurface.withValues(
                      alpha: 0.6,
                    ),
                    minimumSize: const Size(0, 40),
                  ),
                  child: Text(skipLabel),
                ),
              const SizedBox(width: AppSpacing.xxs),
              FilledButton(
                onPressed: onNext,
                style: FilledButton.styleFrom(
                  backgroundColor: accent,
                  foregroundColor: _inkOn(context, accent),
                  minimumSize: const Size(0, 40),
                  padding: const EdgeInsets.symmetric(
                    horizontal: AppSpacing.md + 4,
                  ),
                ),
                child: Text(nextLabel),
              ),
            ],
          ),
        ],
      ),
    );

    final x = pointerX;
    if (x == null) return body;

    // The pointer is placed in physical pixels from the left, whatever the
    // reading direction: it follows the opening, which has no direction.
    final pointer = Directionality(
      textDirection: TextDirection.ltr,
      child: Padding(
        padding: EdgeInsets.only(left: x - _pointer),
        child: Align(
          alignment: Alignment.centerLeft,
          child: CustomPaint(
            size: const Size(_pointer * 2, _pointer),
            painter: _PointerPainter(
              color: surface,
              border: accent.withValues(alpha: 0.28),
              up: pointUp,
            ),
          ),
        ),
      ),
    );

    return Column(
      mainAxisSize: MainAxisSize.min,
      children: [
        if (pointUp) pointer,
        Transform.translate(offset: Offset(0, pointUp ? -1 : 1), child: body),
        if (!pointUp) pointer,
      ],
    );
  }

  /// Text that reads on [fill]. The dark theme's accents are light — green,
  /// sky, amber — and white on them all but disappears.
  static Color _inkOn(BuildContext context, Color fill) {
    if (fill == context.colors.primary) return context.colors.onPrimary;
    return ThemeData.estimateBrightnessForColor(fill) == Brightness.dark
        ? Colors.white
        : Colors.black.withValues(alpha: 0.85);
  }

  Widget _badge(
    BuildContext context, {
    required double size,
    required double icon,
  }) {
    return Container(
      width: size,
      height: size,
      decoration: BoxDecoration(
        color: accent.withValues(alpha: 0.14),
        borderRadius: BorderRadius.circular(size * 0.32),
      ),
      child: Icon(step.icon, size: icon, color: accent),
    );
  }
}

/// Progress through the tour: one dot per step, the current one stretched.
class _Dots extends StatelessWidget {
  const _Dots({required this.index, required this.count, required this.color});

  final int index;
  final int count;
  final Color color;

  @override
  Widget build(BuildContext context) {
    if (count < 2) return const SizedBox.shrink();
    final compact = count > 8;
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        for (var i = 0; i < count; i++)
          AnimatedContainer(
            duration: AppDurations.normal,
            curve: Curves.easeOutCubic,
            margin: EdgeInsets.symmetric(horizontal: compact ? 1.5 : 2.5),
            width: i == index ? (compact ? 12 : 18) : (compact ? 5 : 6),
            height: compact ? 5 : 6,
            decoration: BoxDecoration(
              color: i == index
                  ? color
                  : context.colors.onSurface.withValues(alpha: 0.18),
              borderRadius: BorderRadius.circular(3),
            ),
          ),
      ],
    );
  }
}

/// Darkens everything but the opening, rings the opening, and sends a soft
/// pulse out from it — the "this, here" of the tour.
class _SpotlightPainter extends CustomPainter {
  _SpotlightPainter({
    required this.hole,
    required this.accent,
    required this.pulse,
    required this.ringOpacity,
  });

  final RRect hole;
  final Color accent;

  /// 0→1, repeating.
  final double pulse;

  /// The ring fades in as the opening settles, so it never outlines a shape
  /// in mid-flight.
  final double ringOpacity;

  @override
  void paint(Canvas canvas, Size size) {
    final dim = Path()
      ..fillType = PathFillType.evenOdd
      ..addRect(Offset.zero & size)
      ..addRRect(hole);
    canvas.drawPath(dim, Paint()..color = Colors.black.withValues(alpha: 0.78));

    if (hole.isEmpty || hole.width < 2) return;
    final ring = Curves.easeIn.transform(ringOpacity);

    // The pulse: a ring that grows away from the opening and fades.
    if (pulse > 0) {
      final grow = Curves.easeOut.transform(pulse);
      canvas.drawRRect(
        hole.inflate(2 + grow * 14),
        Paint()
          ..style = PaintingStyle.stroke
          ..strokeWidth = 2.5
          ..color = accent.withValues(alpha: (1 - grow) * 0.7 * ring),
      );
    }

    canvas.drawRRect(
      hole.inflate(2),
      Paint()
        ..style = PaintingStyle.stroke
        ..strokeWidth = 6
        ..color = accent.withValues(alpha: 0.45 * ring)
        ..maskFilter = const MaskFilter.blur(BlurStyle.normal, 6),
    );
    canvas.drawRRect(
      hole.inflate(1.5),
      Paint()
        ..style = PaintingStyle.stroke
        ..strokeWidth = 2.5
        ..color = Colors.white.withValues(alpha: ring),
    );
  }

  @override
  bool shouldRepaint(covariant _SpotlightPainter old) =>
      old.hole != hole ||
      old.pulse != pulse ||
      old.accent != accent ||
      old.ringOpacity != ringOpacity;
}

class _PointerPainter extends CustomPainter {
  _PointerPainter({
    required this.color,
    required this.border,
    required this.up,
  });

  final Color color;
  final Color border;
  final bool up;

  @override
  void paint(Canvas canvas, Size size) {
    final w = size.width, h = size.height;
    final path = up
        ? (Path()
            ..moveTo(0, h)
            ..lineTo(w / 2, 0)
            ..lineTo(w, h))
        : (Path()
            ..moveTo(0, 0)
            ..lineTo(w / 2, h)
            ..lineTo(w, 0));
    canvas.drawPath(path..close(), Paint()..color = color);
    // Only the two slanted sides carry the border; the base merges with the
    // card's own edge.
    final edge = up
        ? (Path()
            ..moveTo(0, h)
            ..lineTo(w / 2, 0)
            ..lineTo(w, h))
        : (Path()
            ..moveTo(0, 0)
            ..lineTo(w / 2, h)
            ..lineTo(w, 0));
    canvas.drawPath(
      edge,
      Paint()
        ..style = PaintingStyle.stroke
        ..strokeWidth = 1
        ..color = border,
    );
  }

  @override
  bool shouldRepaint(covariant _PointerPainter old) =>
      old.color != color || old.border != border || old.up != up;
}
