import 'dart:async';

import 'package:flutter/material.dart';

import '../api/wordos_api.dart';
import '../l10n/app_strings.dart';
import '../theme/app_tokens.dart';

/// Reusable presentation building blocks shared by every feature.

class AppCard extends StatelessWidget {
  const AppCard({
    super.key,
    required this.child,
    this.onTap,
    this.padding = const EdgeInsets.all(AppSpacing.md),
    this.color,
    this.borderColor,
  });

  final Widget child;
  final VoidCallback? onTap;
  final EdgeInsets padding;
  final Color? color;
  final Color? borderColor;

  @override
  Widget build(BuildContext context) {
    final decorated = Container(
      padding: padding,
      decoration: BoxDecoration(
        color: color ?? context.colors.surface,
        borderRadius: AppRadii.cardBorder,
        border: Border.all(color: borderColor ?? context.palette.border),
      ),
      child: child,
    );

    if (onTap == null) return decorated;
    return Material(
      color: Colors.transparent,
      child: InkWell(
        onTap: onTap,
        borderRadius: AppRadii.cardBorder,
        child: decorated,
      ),
    );
  }
}

class SectionHeader extends StatelessWidget {
  const SectionHeader({super.key, required this.title, this.subtitle, this.trailing});

  final String title;
  final String? subtitle;
  final Widget? trailing;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(bottom: AppSpacing.sm),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(title, style: context.text.titleMedium),
                if (subtitle != null) ...[
                  const SizedBox(height: AppSpacing.xxs),
                  Text(
                    subtitle!,
                    style: context.text.bodySmall?.copyWith(
                      color: context.colors.onSurface.withValues(alpha: 0.6),
                    ),
                  ),
                ],
              ],
            ),
          ),
          ?trailing,
        ],
      ),
    );
  }
}

class StatusPill extends StatelessWidget {
  const StatusPill({
    super.key,
    required this.label,
    required this.color,
    this.icon,
    this.filled = true,
  });

  final String label;
  final Color color;
  final IconData? icon;
  final bool filled;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(
        horizontal: AppSpacing.xs + 2,
        vertical: 5,
      ),
      decoration: BoxDecoration(
        color: filled ? color.withValues(alpha: 0.13) : Colors.transparent,
        borderRadius: AppRadii.chipBorder,
        border: Border.all(color: color.withValues(alpha: filled ? 0.24 : 0.5)),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          if (icon != null) ...[
            Icon(icon, size: 14, color: color),
            const SizedBox(width: 4),
          ],
          // Long labels (Arabic meanings, skill + status combinations) must
          // shrink rather than overflow the pill.
          Flexible(
            child: Text(
              label,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: context.text.labelSmall?.copyWith(
                color: color,
                fontWeight: FontWeight.w700,
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class EmptyState extends StatelessWidget {
  const EmptyState({
    super.key,
    required this.icon,
    required this.title,
    this.message,
    this.action,
  });

  final IconData icon;
  final String title;
  final String? message;
  final Widget? action;

  @override
  Widget build(BuildContext context) {
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(AppSpacing.lg),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Container(
              width: 72,
              height: 72,
              decoration: BoxDecoration(
                color: context.palette.subtleSurface,
                shape: BoxShape.circle,
              ),
              child: Icon(icon, size: 32, color: context.colors.primary),
            ),
            const SizedBox(height: AppSpacing.md),
            Text(
              title,
              style: context.text.titleMedium,
              textAlign: TextAlign.center,
            ),
            if (message != null) ...[
              const SizedBox(height: AppSpacing.xs),
              Text(
                message!,
                textAlign: TextAlign.center,
                style: context.text.bodyMedium?.copyWith(
                  color: context.colors.onSurface.withValues(alpha: 0.6),
                ),
              ),
            ],
            if (action != null) ...[
              const SizedBox(height: AppSpacing.lg),
              action!,
            ],
          ],
        ),
      ),
    );
  }
}

class ErrorView extends StatefulWidget {
  const ErrorView({
    super.key,
    required this.message,
    required this.retryLabel,
    this.onRetry,
    this.healsItself = false,
  });

  /// The usual way to build one: from the failure itself.
  ///
  /// It picks the sentence — a learner with no signal is told they have no
  /// signal, not "something went wrong" — and it turns on the self-healing for
  /// exactly the failures that can heal (`ApiException.isRetryable`). A word
  /// that does not exist will refuse identically for ever and is not retried.
  factory ErrorView.from(
    Object error,
    AppStrings strings, {
    Key? key,
    VoidCallback? onRetry,
  }) {
    final failure = ApiException.from(error);

    return ErrorView(
      key: key,
      message: failure.code == 'UNEXPECTED'
          ? strings.somethingWentWrong
          : strings.apiError(failure.code, failure.message),
      retryLabel: strings.retry,
      onRetry: onRetry,
      healsItself: failure.isRetryable,
    );
  }

  final String message;
  final String retryLabel;
  final VoidCallback? onRetry;

  /// Whether this screen retries on its own while it is on display.
  ///
  /// The learner should not have to press anything when the connection comes
  /// back. They are holding a phone that reconnects silently, and a screen
  /// that keeps saying "no connection" over a working network is the app being
  /// wrong about the world (ADR-093).
  final bool healsItself;

  @override
  State<ErrorView> createState() => _ErrorViewState();
}

class _ErrorViewState extends State<ErrorView> with WidgetsBindingObserver {
  Timer? _timer;

  /// How long to wait before trying again, doubling to a ceiling.
  ///
  /// It starts short because most of these clear in seconds — a tunnel, a lift,
  /// a server waking up — and backs off because the one case that does not
  /// clear must not become a request every two seconds for as long as the
  /// screen is open.
  static const Duration _first = Duration(seconds: 2);
  static const Duration _ceiling = Duration(seconds: 20);
  Duration _wait = _first;

  @override
  void initState() {
    super.initState();
    if (!widget.healsItself || widget.onRetry == null) return;

    WidgetsBinding.instance.addObserver(this);
    _schedule();
  }

  @override
  void didUpdateWidget(ErrorView oldWidget) {
    super.didUpdateWidget(oldWidget);

    // A different failure is a fresh start: the backoff earned by the last one
    // should not make the next one wait twenty seconds for its first attempt.
    if (widget.message != oldWidget.message) {
      _wait = _first;
      _schedule();
    }
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    // Coming back to the app is the single strongest signal that something may
    // have changed — and it is the moment the learner is looking at the screen,
    // so it is the moment a stale error is most worth clearing.
    if (state == AppLifecycleState.resumed) {
      _wait = _first;
      _retry();
    }
  }

  void _schedule() {
    _timer?.cancel();
    _timer = Timer(_wait, _retry);
  }

  void _retry() {
    if (!mounted) return;

    // Doubling *before* the attempt, so a failure that repeats immediately
    // still backs off rather than retrying at the same rate for ever.
    _wait = _wait * 2 > _ceiling ? _ceiling : _wait * 2;

    widget.onRetry?.call();

    // Rescheduled unconditionally: this widget is rebuilt out of existence the
    // moment the data arrives, so "still here" is the same fact as "it has not
    // worked yet".
    _schedule();
  }

  @override
  void dispose() {
    _timer?.cancel();
    WidgetsBinding.instance.removeObserver(this);
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return EmptyState(
      icon: Icons.error_outline_rounded,
      title: widget.message,
      // The button stays, even while this retries on its own. A learner who
      // wants to do something about it should be able to, and waiting for a
      // timer they cannot see is its own kind of stuck.
      action: widget.onRetry == null
          ? null
          : OutlinedButton.icon(
              onPressed: widget.onRetry,
              icon: const Icon(Icons.refresh_rounded),
              label: Text(widget.retryLabel),
            ),
    );
  }
}

/// A multiple-choice option that reflects answer feedback.
class OptionTile extends StatelessWidget {
  const OptionTile({
    super.key,
    required this.label,
    required this.onTap,
    this.selected = false,
    this.correct,
    this.enabled = true,
  });

  final String label;
  final VoidCallback onTap;
  final bool selected;

  /// null = not yet answered, true = this option is the right one,
  /// false = this option was chosen and is wrong.
  final bool? correct;
  final bool enabled;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    Color border = palette.border;
    Color background = context.colors.surface;
    Color foreground = context.colors.onSurface;
    IconData? trailing;

    if (correct == true) {
      border = palette.success;
      background = palette.successSurface;
      foreground = palette.success;
      trailing = Icons.check_circle_rounded;
    } else if (correct == false) {
      border = palette.danger;
      background = palette.dangerSurface;
      foreground = palette.danger;
      trailing = Icons.cancel_rounded;
    } else if (selected) {
      border = context.colors.primary;
      background = context.colors.primary.withValues(alpha: 0.08);
    }

    return Padding(
      padding: const EdgeInsets.only(bottom: AppSpacing.xs),
      child: Material(
        color: Colors.transparent,
        child: InkWell(
          onTap: enabled ? onTap : null,
          borderRadius: AppRadii.fieldBorder,
          child: AnimatedContainer(
            duration: AppDurations.fast,
            padding: const EdgeInsets.symmetric(
              horizontal: AppSpacing.md,
              vertical: AppSpacing.md,
            ),
            decoration: BoxDecoration(
              color: background,
              borderRadius: AppRadii.fieldBorder,
              border: Border.all(
                color: border,
                width: correct != null || selected ? 1.6 : 1,
              ),
            ),
            child: Row(
              children: [
                Expanded(
                  // An option is English on a comprehension question and
                  // Arabic on a word question, and this widget renders both —
                  // so the direction follows the text rather than the
                  // interface. Inheriting Arabic put the full stop of an
                  // English option at the front of it.
                  child: AutoDirectionText(
                    label,
                    style: context.text.bodyLarge?.copyWith(
                      color: foreground,
                      fontWeight: correct != null ? FontWeight.w600 : null,
                    ),
                  ),
                ),
                if (trailing != null) Icon(trailing, color: foreground, size: 20),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

class StepProgressBar extends StatelessWidget {
  const StepProgressBar({
    super.key,
    required this.value,
    this.animate = false,
    this.height = 8,
  });

  final double value;

  /// Whether the bar slides to a new value instead of jumping to it.
  ///
  /// Off by default, because most callers redraw a bar that has not moved and
  /// an animation on every rebuild is a bar that never sits still. On where the
  /// movement is the point — the weekly challenge, where the bar is the only
  /// progress the learner is shown (ADR-091).
  final bool animate;

  final double height;

  @override
  Widget build(BuildContext context) {
    final target = value.clamp(0.0, 1.0);

    return ClipRRect(
      borderRadius: BorderRadius.circular(999),
      child: animate
          ? TweenAnimationBuilder<double>(
              tween: Tween(begin: 0, end: target),
              // Long enough to be seen as movement rather than a redraw, short
              // enough that the next question is not waiting on it.
              duration: const Duration(milliseconds: 650),
              curve: Curves.easeOutCubic,
              builder: (context, shown, _) => LinearProgressIndicator(
                value: shown,
                minHeight: height,
                backgroundColor: context.palette.subtleSurface,
              ),
            )
          : LinearProgressIndicator(
              value: target,
              minHeight: height,
              backgroundColor: context.palette.subtleSurface,
            ),
    );
  }
}

/// How full the weekly challenge's bar looks after [done] of [total] words.
///
/// **Deliberately not `done / total`.** A sitting of fifty words is fifty
/// questions, and a linear bar answers the first one by moving two per cent —
/// which reads as *nothing happened*. The learner most likely to quit is the
/// one who has answered three questions and cannot see that they have, and that
/// is the moment this curve is for (ADR-091).
///
/// So the first few move fast and the rest settle down: the opening stretch of
/// the bar is spent on the opening handful of words, and the remainder is
/// shared out evenly across everything after them. It is a presentation curve
/// and nothing else — the score, the queue and the result are counted honestly
/// and are not touched by it.
///
/// It never overstates the end. At `done == total` it is exactly 1, so the bar
/// arrives full at the same moment the challenge does; the flattery is all in
/// the middle, where it costs nothing and buys the thing it is for.
double challengeProgress(int done, int total) {
  if (total <= 0 || done <= 0) return 0;
  if (done >= total) return 1;

  /// The opening words that move the bar quickly.
  const fastWords = 5;

  /// How much of the bar they are given between them.
  const fastShare = 0.35;

  // A short challenge is all "opening", so the curve would be the whole bar.
  // Below this it stays linear rather than sprinting to nearly full on the
  // second answer, which would be the same lie in the other direction.
  if (total <= fastWords + 1) return done / total;

  if (done <= fastWords) return fastShare * (done / fastWords);

  final after = (done - fastWords) / (total - fastWords);
  return fastShare + (1 - fastShare) * after;
}

/// English content, laid out left-to-right whatever the interface language.
///
/// The learning content is English; the interface may be Arabic. Without this
/// the passage inherits the app's RTL direction and every line is right-aligned
/// and read from the wrong end — the text is still legible, and still wrong.
///
/// Wrap English *content*: passages, words, example sentences. Not interface
/// copy, which should follow the interface.
class EnglishText extends StatelessWidget {
  const EnglishText(
    this.data, {
    super.key,
    this.style,
    this.textAlign,
    this.maxLines,
    this.overflow,
  });

  final String data;
  final TextStyle? style;
  final TextAlign? textAlign;

  /// Both default to null, which is `Text`'s own behaviour — prose wraps and is
  /// never clipped. They exist for the places where the English sits inside a
  /// fixed box, such as a button label, where a long word would otherwise
  /// overflow rather than wrap.
  final int? maxLines;
  final TextOverflow? overflow;

  /// The edge the surrounding interface starts from — right in Arabic, left in
  /// English — as an absolute alignment.
  ///
  /// For English that sits *inside* interface layout rather than standing on
  /// its own: a dictionary definition under an Arabic meaning, in a card whose
  /// every other line hangs from the right. Left-aligning that one line would
  /// tear it away from the meaning it explains. So the line keeps the card's
  /// edge, and only its *direction* is English — which is the part that moves
  /// the punctuation. Absolute rather than `start`, because inside this widget
  /// `start` would mean the English side.
  static TextAlign interfaceStart(BuildContext context) =>
      Directionality.of(context) == TextDirection.rtl
          ? TextAlign.right
          : TextAlign.left;

  @override
  Widget build(BuildContext context) => Directionality(
        textDirection: TextDirection.ltr,
        child: Text(
          data,
          style: style,
          textAlign: textAlign ?? TextAlign.left,
          maxLines: maxLines,
          overflow: overflow,
        ),
      );
}

/// Text whose direction follows **the text**, not the interface.
///
/// For content that may be either language and is not known which at the call
/// site: a comprehension question and its options are English, a word question
/// asks in English about Arabic meanings, and the same widget renders both.
///
/// Inheriting the Arabic interface's direction is not merely untidy for
/// English — it moves the punctuation. `What does "fan" mean here?` renders as
/// `?What does "fan" mean here`, with the question mark leading the sentence,
/// because a trailing neutral character in an RTL paragraph belongs to the
/// paragraph rather than to the words. The sentence stays legible and reads as
/// though the app were broken.
///
/// [EnglishText] is still the right widget wherever the content is known to be
/// English — the passage, a target word — because it states that fact rather
/// than inferring it.
class AutoDirectionText extends StatelessWidget {
  const AutoDirectionText(
    this.data, {
    super.key,
    this.style,
    this.textAlign,
  });

  final String data;
  final TextStyle? style;
  final TextAlign? textAlign;

  /// The direction of the first strongly-directional character, or null when
  /// there is none — digits and punctuation alone say nothing about language,
  /// and guessing from them would be worse than following the interface.
  static TextDirection? directionOf(String text) {
    for (final rune in text.runes) {
      // Arabic, Arabic Supplement/Extended, Presentation Forms, and Hebrew.
      if ((rune >= 0x0590 && rune <= 0x08FF) ||
          (rune >= 0xFB1D && rune <= 0xFDFF) ||
          (rune >= 0xFE70 && rune <= 0xFEFF)) {
        return TextDirection.rtl;
      }
      if ((rune >= 0x0041 && rune <= 0x005A) ||
          (rune >= 0x0061 && rune <= 0x007A) ||
          (rune >= 0x00C0 && rune <= 0x024F)) {
        return TextDirection.ltr;
      }
    }
    return null;
  }

  @override
  Widget build(BuildContext context) {
    final direction = directionOf(data);
    if (direction == null) {
      return Text(data, style: style, textAlign: textAlign);
    }

    return Directionality(
      textDirection: direction,
      child: Text(
        data,
        style: style,
        textAlign: textAlign ??
            (direction == TextDirection.rtl
                ? TextAlign.right
                : TextAlign.left),
      ),
    );
  }
}

class LevelBadge extends StatelessWidget {
  const LevelBadge({
    super.key,
    required this.label,
    this.color,
    this.size,
    this.trailing,
  });

  final String label;
  final Color? color;

  /// Font size. The default suits a badge beside body text; a badge that is
  /// itself a control needs to be readable at a glance.
  final double? size;

  /// An icon after the label — a chevron, when the badge can be tapped.
  final IconData? trailing;

  @override
  Widget build(BuildContext context) {
    final c = color ?? context.colors.primary;
    final style = context.text.labelSmall
        ?.copyWith(color: c, fontWeight: FontWeight.w700, fontSize: size);

    return Container(
      padding: EdgeInsets.symmetric(
        horizontal: trailing == null ? 8 : 10,
        vertical: size == null ? 3 : 5,
      ),
      decoration: BoxDecoration(
        color: c.withValues(alpha: 0.12),
        borderRadius: BorderRadius.circular(999),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          // The band is always Latin, whatever the interface language.
          Directionality(
            textDirection: TextDirection.ltr,
            child: Text(label, style: style),
          ),
          if (trailing != null) ...[
            const SizedBox(width: 2),
            Icon(trailing, size: (size ?? 12) + 4, color: c),
          ],
        ],
      ),
    );
  }
}

/// Full-screen busy state used while the AI-backed endpoints work.
class BusyView extends StatelessWidget {
  const BusyView({super.key, required this.message});

  final String message;

  @override
  Widget build(BuildContext context) {
    return Center(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          const SizedBox(
            width: 34,
            height: 34,
            child: CircularProgressIndicator(strokeWidth: 3),
          ),
          const SizedBox(height: AppSpacing.md),
          Text(
            message,
            style: context.text.bodyMedium?.copyWith(
              color: context.colors.onSurface.withValues(alpha: 0.65),
            ),
          ),
        ],
      ),
    );
  }
}

/// What replaces a widget that threw while building (see `main.dart`).
///
/// Deliberately the plainest thing in the app. It cannot read the theme — the
/// failure may be *in* the theme, or in whatever was meant to provide it — and
/// it cannot read [AppStrings] either, since the same is true of the locale.
/// So it is self-contained: its own colours, and a bilingual line that needs no
/// lookup to be right in either language.
///
/// It is also deliberately not a retry button. The widget that failed will fail
/// again the moment it rebuilds, and offering an action that cannot work is how
/// a learner ends up tapping the same button until they give up. Backing out to
/// the previous screen is the way forward, and that control is already there.
class AppErrorBox extends StatelessWidget {
  const AppErrorBox({super.key});

  @override
  Widget build(BuildContext context) {
    return Container(
      color: const Color(0xFFFDF6F4),
      alignment: Alignment.center,
      padding: const EdgeInsets.all(AppSpacing.lg),
      child: const Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(Icons.error_outline_rounded, size: 34, color: Color(0xFFB3261E)),
          SizedBox(height: AppSpacing.sm),
          Text(
            'حدث خطأ ما\nSomething went wrong',
            textAlign: TextAlign.center,
            textDirection: TextDirection.rtl,
            style: TextStyle(
              fontSize: 15,
              height: 1.5,
              color: Color(0xFF1C1B1F),
              decoration: TextDecoration.none,
            ),
          ),
        ],
      ),
    );
  }
}
