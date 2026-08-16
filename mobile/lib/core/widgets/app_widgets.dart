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

class LevelBadge extends StatelessWidget {
  const LevelBadge({super.key, required this.label, this.color});

  final String label;
  final Color? color;

  @override
  Widget build(BuildContext context) {
    final c = color ?? context.colors.primary;
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
      decoration: BoxDecoration(
        color: c.withValues(alpha: 0.12),
        borderRadius: BorderRadius.circular(999),
      ),
      child: Text(
        label,
        style: context.text.labelSmall
            ?.copyWith(color: c, fontWeight: FontWeight.w700),
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
