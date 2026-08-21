import 'package:flutter/material.dart';

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

class ErrorView extends StatelessWidget {
  const ErrorView({
    super.key,
    required this.message,
    required this.retryLabel,
    this.onRetry,
  });

  final String message;
  final String retryLabel;
  final VoidCallback? onRetry;

  @override
  Widget build(BuildContext context) {
    return EmptyState(
      icon: Icons.error_outline_rounded,
      title: message,
      action: onRetry == null
          ? null
          : OutlinedButton.icon(
              onPressed: onRetry,
              icon: const Icon(Icons.refresh_rounded),
              label: Text(retryLabel),
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
                  child: Text(
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
  const StepProgressBar({super.key, required this.value});

  final double value;

  @override
  Widget build(BuildContext context) {
    return ClipRRect(
      borderRadius: BorderRadius.circular(999),
      child: LinearProgressIndicator(
        value: value.clamp(0.0, 1.0),
        minHeight: 8,
        backgroundColor: context.palette.subtleSurface,
      ),
    );
  }
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
  const EnglishText(this.data, {super.key, this.style, this.textAlign});

  final String data;
  final TextStyle? style;
  final TextAlign? textAlign;

  @override
  Widget build(BuildContext context) => Directionality(
        textDirection: TextDirection.ltr,
        child: Text(data, style: style, textAlign: textAlign ?? TextAlign.left),
      );
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
