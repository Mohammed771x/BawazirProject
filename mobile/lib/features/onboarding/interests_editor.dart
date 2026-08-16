import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/l10n/app_strings.dart';
import '../../core/models/models.dart';
import '../../core/theme/app_tokens.dart';

/// The one place interests are picked, so onboarding and Settings can never
/// drift apart.
///
/// The catalogue is open on purpose: "Other" lets a learner type an interest we
/// have never seen, which is exactly the signal we want for personalisation
/// later (demo review §3.1). A typed interest is sent as free text; the backend
/// decides whether it matches a known slug or is stored as `is_custom`.
class InterestsEditor extends ConsumerWidget {
  const InterestsEditor({
    super.key,
    required this.options,
    required this.selected,
    required this.onChanged,
    this.enabled = true,
  });

  final List<InterestOption> options;
  final Set<String> selected;
  final ValueChanged<Set<String>> onChanged;
  final bool enabled;

  /// Anything selected that is not a known catalogue slug is a custom entry.
  static Iterable<String> customOf(
    Iterable<String> selected,
    List<InterestOption> options,
  ) {
    final slugs = options.map((o) => o.slug).toSet();
    return selected.where((i) => !slugs.contains(i));
  }

  void _toggle(String value) {
    final next = Set<String>.from(selected);
    next.contains(value) ? next.remove(value) : next.add(value);
    onChanged(next);
  }

  Future<void> _addCustom(BuildContext context, AppStrings s) async {
    final value = await showDialog<String>(
      context: context,
      builder: (context) => _CustomInterestDialog(strings: s),
    );
    final trimmed = value?.trim() ?? '';
    if (trimmed.isEmpty) return;

    // Case-insensitive de-duplication against both the catalogue and what is
    // already selected, so "Football" cannot be added next to "football".
    final lower = trimmed.toLowerCase();
    final existingSlug = options
        .where((o) =>
            o.slug.toLowerCase() == lower ||
            o.labelEn.toLowerCase() == lower ||
            o.labelAr == trimmed)
        .firstOrNull;
    final value0 = existingSlug?.slug ?? trimmed;
    if (selected.any((i) => i.toLowerCase() == value0.toLowerCase())) return;

    onChanged({...selected, value0});
  }

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    final custom = customOf(selected, options).toList()..sort();

    return Wrap(
      spacing: AppSpacing.xs,
      runSpacing: AppSpacing.xs,
      children: [
        for (final option in options)
          InterestChip(
            label:
                '${option.emoji}  ${s.isArabic ? option.labelAr : option.labelEn}',
            selected: selected.contains(option.slug),
            onTap: enabled ? () => _toggle(option.slug) : null,
          ),
        for (final entry in custom)
          InterestChip(
            label: '✨  $entry',
            selected: true,
            onTap: enabled ? () => _toggle(entry) : null,
          ),
        InterestChip(
          label: '➕  ${s.otherInterest}',
          selected: false,
          onTap: enabled ? () => _addCustom(context, s) : null,
        ),
      ],
    );
  }
}

class _CustomInterestDialog extends StatefulWidget {
  const _CustomInterestDialog({required this.strings});

  final AppStrings strings;

  @override
  State<_CustomInterestDialog> createState() => _CustomInterestDialogState();
}

class _CustomInterestDialogState extends State<_CustomInterestDialog> {
  final _controller = TextEditingController();

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  void _submit() => Navigator.of(context).pop(_controller.text.trim());

  @override
  Widget build(BuildContext context) {
    final s = widget.strings;
    return AlertDialog(
      title: Text(s.addInterest),
      content: TextField(
        controller: _controller,
        autofocus: true,
        textCapitalization: TextCapitalization.words,
        maxLength: 40,
        decoration: InputDecoration(
          hintText: s.customInterestHint,
          counterText: '',
        ),
        onSubmitted: (_) => _submit(),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: Text(s.cancel),
        ),
        FilledButton(onPressed: _submit, child: Text(s.save)),
      ],
    );
  }
}

class InterestChip extends StatelessWidget {
  const InterestChip({
    super.key,
    required this.label,
    required this.selected,
    required this.onTap,
  });

  final String label;
  final bool selected;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) {
    return Material(
      color: Colors.transparent,
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(999),
        child: AnimatedContainer(
          duration: AppDurations.fast,
          padding: const EdgeInsets.symmetric(
            horizontal: AppSpacing.md,
            vertical: AppSpacing.sm,
          ),
          decoration: BoxDecoration(
            color: selected
                ? context.colors.primary.withValues(alpha: 0.12)
                : context.colors.surface,
            borderRadius: BorderRadius.circular(999),
            border: Border.all(
              color: selected ? context.colors.primary : context.palette.border,
              width: selected ? 1.6 : 1,
            ),
          ),
          child: Text(
            label,
            style: context.text.labelLarge?.copyWith(
              color: selected ? context.colors.primary : null,
            ),
          ),
        ),
      ),
    );
  }
}
