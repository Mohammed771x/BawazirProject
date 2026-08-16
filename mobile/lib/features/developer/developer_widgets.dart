import 'package:flutter/material.dart';

import '../../core/theme/app_tokens.dart';
import '../../core/widgets/app_widgets.dart';

/// Small, dependency-free charting primitives for the Owner dashboard.
///
/// Deliberately hand-drawn rather than pulling in a charting package: these are
/// three simple shapes, and an extra dependency for them would be poor value
/// (the brief asks not to add dependencies unnecessarily).

/// A single headline number.
class MetricTile extends StatelessWidget {
  const MetricTile({
    super.key,
    required this.label,
    required this.value,
    this.caption,
    this.tone,
  });

  final String label;
  final String value;
  final String? caption;
  final Color? tone;

  @override
  Widget build(BuildContext context) {
    // Every line is single-line-and-clipped on purpose: these tiles live in a
    // fixed-aspect-ratio grid, so a label or caption that wraps would overflow
    // the cell rather than grow it.
    return AppCard(
      padding: const EdgeInsets.all(AppSpacing.sm),
      // The Column fills the cell (not MainAxisSize.min) so the Flexible value
      // actually has room to shrink into; with a min-sized column the three
      // lines' intrinsic heights add up and overflow the fixed-ratio cell.
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        mainAxisAlignment: MainAxisAlignment.center,
        children: [
          Text(
            label,
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            style: context.text.labelSmall?.copyWith(
              color: context.colors.onSurface.withValues(alpha: 0.6),
            ),
          ),
          Flexible(
            child: FittedBox(
              fit: BoxFit.scaleDown,
              alignment: AlignmentDirectional.centerStart,
              child: Text(
                value,
                maxLines: 1,
                style: context.text.titleLarge?.copyWith(
                  color: tone ?? context.colors.onSurface,
                  fontWeight: FontWeight.w700,
                ),
              ),
            ),
          ),
          if (caption != null)
            Text(
              caption!,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: context.text.labelSmall?.copyWith(
                color: context.colors.onSurface.withValues(alpha: 0.5),
              ),
            ),
        ],
      ),
    );
  }
}

/// Horizontal bars — used for pass rates, failure distribution and interests.
class BarChart extends StatelessWidget {
  const BarChart({super.key, required this.bars, this.valueLabel});

  final List<ChartBar> bars;

  /// Formats the trailing label. Defaults to a percentage.
  final String Function(double value)? valueLabel;

  @override
  Widget build(BuildContext context) {
    if (bars.isEmpty) {
      return Text(
        '—',
        style: context.text.bodySmall?.copyWith(
          color: context.colors.onSurface.withValues(alpha: 0.5),
        ),
      );
    }

    // Bars are scaled to the largest value present, not to 1.0, so a chart of
    // small numbers is still readable.
    final peak = bars.map((b) => b.value).fold(0.0, (a, b) => a > b ? a : b);
    final scale = peak <= 0 ? 1.0 : peak;
    final format =
        valueLabel ?? (v) => '${(v * 100).toStringAsFixed(0)}%';

    return Column(
      children: [
        for (final bar in bars)
          Padding(
            padding: const EdgeInsets.only(bottom: AppSpacing.xs),
            child: Row(
              children: [
                SizedBox(
                  width: 86,
                  child: Text(
                    bar.label,
                    style: context.text.labelSmall,
                    overflow: TextOverflow.ellipsis,
                  ),
                ),
                const SizedBox(width: AppSpacing.xs),
                Expanded(
                  child: ClipRRect(
                    borderRadius: BorderRadius.circular(999),
                    child: LinearProgressIndicator(
                      value: (bar.value / scale).clamp(0.0, 1.0),
                      minHeight: 10,
                      backgroundColor:
                          context.colors.onSurface.withValues(alpha: 0.07),
                      valueColor: AlwaysStoppedAnimation(
                        bar.color ?? context.colors.primary,
                      ),
                    ),
                  ),
                ),
                const SizedBox(width: AppSpacing.xs),
                SizedBox(
                  width: 46,
                  child: Text(
                    format(bar.value),
                    textAlign: TextAlign.end,
                    style: context.text.labelSmall?.copyWith(
                      color: context.colors.onSurface.withValues(alpha: 0.7),
                    ),
                  ),
                ),
              ],
            ),
          ),
      ],
    );
  }
}

class ChartBar {
  const ChartBar({required this.label, required this.value, this.color});

  final String label;
  final double value;
  final Color? color;
}

/// Vertical columns for a day-by-day series.
class ColumnChart extends StatelessWidget {
  const ColumnChart({super.key, required this.columns, this.height = 96});

  final List<ChartColumn> columns;
  final double height;

  @override
  Widget build(BuildContext context) {
    final peak =
        columns.map((c) => c.value).fold(0.0, (a, b) => a > b ? a : b);
    final scale = peak <= 0 ? 1.0 : peak;

    return SizedBox(
      height: height,
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.end,
        children: [
          for (final column in columns)
            Expanded(
              child: Padding(
                padding: const EdgeInsets.symmetric(horizontal: 1.5),
                // The bar takes whatever vertical space the labels leave, via
                // Expanded + a height factor. Computing its pixel height by
                // hand meant guessing the labels' line heights, which is how
                // this overflowed by a couple of pixels.
                child: Column(
                  mainAxisAlignment: MainAxisAlignment.end,
                  children: [
                    if (column.value > 0)
                      Text(
                        column.value.toStringAsFixed(0),
                        style: context.text.labelSmall?.copyWith(fontSize: 9),
                        maxLines: 1,
                      ),
                    Expanded(
                      child: FractionallySizedBox(
                        alignment: Alignment.bottomCenter,
                        heightFactor:
                            (column.value / scale).clamp(0.03, 1.0),
                        child: Container(
                          decoration: BoxDecoration(
                            color: column.color ?? context.colors.primary,
                            borderRadius: const BorderRadius.vertical(
                              top: Radius.circular(3),
                            ),
                          ),
                        ),
                      ),
                    ),
                    const SizedBox(height: 3),
                    Text(
                      column.label,
                      style: context.text.labelSmall?.copyWith(
                        fontSize: 8,
                        color: context.colors.onSurface.withValues(alpha: 0.5),
                      ),
                      maxLines: 1,
                      overflow: TextOverflow.clip,
                    ),
                  ],
                ),
              ),
            ),
        ],
      ),
    );
  }
}

class ChartColumn {
  const ChartColumn({required this.label, required this.value, this.color});

  final String label;
  final double value;
  final Color? color;
}
