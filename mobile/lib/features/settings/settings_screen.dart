import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../app/router.dart';

import '../../core/api/api_providers.dart';
import '../../core/api/wordos_api.dart';
import '../../core/l10n/app_strings.dart';
import '../../core/models/models.dart';
import '../../core/storage/preferences_providers.dart';
import '../../core/theme/app_tokens.dart';
import '../../core/theme/skill_visuals.dart';
import '../../core/widgets/app_widgets.dart';
import '../auth/session_controller.dart';
import '../hub/hub_screen.dart';
import '../onboarding/interests_editor.dart';
import '../onboarding/interests_screen.dart';

final configProvider = FutureProvider<PublicConfig>(
  (ref) => ref.watch(wordOsApiProvider).config(),
);

class SettingsScreen extends ConsumerWidget {
  const SettingsScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    final user = ref.watch(sessionProvider).user;
    final config =
        ref.watch(configProvider).valueOrNull ?? PublicConfig.fallback;

    if (user == null) return const SizedBox.shrink();

    return Scaffold(
      appBar: AppBar(title: Text(s.settings)),
      body: ListView(
        padding: const EdgeInsets.fromLTRB(
          AppSpacing.md,
          AppSpacing.md,
          AppSpacing.md,
          AppSpacing.xxl,
        ),
        children: [
          _ProfileCard(user: user),
          const SizedBox(height: AppSpacing.lg),
          SectionHeader(title: s.skillLevels, subtitle: s.levelExplainer),
          for (final level in user.skillLevels) ...[
            _SkillLevelCard(level: level),
            const SizedBox(height: AppSpacing.xs),
          ],
          const SizedBox(height: AppSpacing.lg),
          SectionHeader(
            title: s.dailyTargets,
            subtitle: s.dailyTargetsExplainer,
          ),
          for (final level in user.skillLevels) ...[
            _DailyTargetCard(level: level, config: config),
            const SizedBox(height: AppSpacing.xs),
          ],
          const SizedBox(height: AppSpacing.lg),
          SectionHeader(title: s.interests),
          _InterestsCard(user: user),
          const SizedBox(height: AppSpacing.lg),
          SectionHeader(title: s.appearance),
          const _AppearanceCard(),
          const SizedBox(height: AppSpacing.lg),
          // Owner-only doorway. A normal account never renders this, and the
          // route guard plus the API's role check both refuse it anyway
          // (demo review §13).
          if (user.role == UserRole.owner) ...[
            _OwnerEntryCard(),
            const SizedBox(height: AppSpacing.lg),
          ],
          OutlinedButton.icon(
            onPressed: () => ref.read(sessionProvider.notifier).signOut(),
            icon: const Icon(Icons.logout_rounded),
            label: Text(s.signOut),
          ),
        ],
      ),
    );
  }
}

class _ProfileCard extends ConsumerWidget {
  const _ProfileCard({required this.user});

  final UserProfile user;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final initials = user.displayName.trim().isEmpty
        ? '?'
        : user.displayName.trim()[0].toUpperCase();
    return AppCard(
      child: Row(
        children: [
          CircleAvatar(
            radius: 26,
            backgroundColor: context.colors.primary.withValues(alpha: 0.14),
            child: Text(
              initials,
              style: context.text.titleLarge
                  ?.copyWith(color: context.colors.primary),
            ),
          ),
          const SizedBox(width: AppSpacing.sm),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(user.displayName, style: context.text.titleMedium),
                Text(
                  user.email,
                  style: context.text.bodySmall?.copyWith(
                    color: context.colors.onSurface.withValues(alpha: 0.6),
                  ),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

class _SkillLevelCard extends ConsumerStatefulWidget {
  const _SkillLevelCard({required this.level});

  final SkillLevel level;

  @override
  ConsumerState<_SkillLevelCard> createState() => _SkillLevelCardState();
}

class _SkillLevelCardState extends ConsumerState<_SkillLevelCard> {
  bool _saving = false;

  Future<void> _update(CefrLevel level) async {
    setState(() => _saving = true);
    try {
      await ref.read(wordOsApiProvider).updateSkillLevel(
            skill: widget.level.skill,
            level: level,
          );
      await ref.read(sessionProvider.notifier).refresh();
      ref.invalidate(hubProvider);
    } on ApiException catch (e) {
      if (mounted) {
        ScaffoldMessenger.of(context)
            .showSnackBar(SnackBar(content: Text(e.message)));
      }
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final s = ref.watch(stringsProvider);
    final color = SkillVisuals.color(context, widget.level.skill);

    return AppCard(
      child: Row(
        children: [
          Icon(SkillVisuals.icon(widget.level.skill), color: color, size: 22),
          const SizedBox(width: AppSpacing.sm),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(s.skillName(widget.level.skill),
                    style: context.text.titleSmall),
                const SizedBox(height: 2),
                Text(
                  widget.level.carriesCefrLevel
                      ? '${s.systemLevel}: '
                          '${widget.level.systemAssessedLevel?.label ?? '—'}'
                      : s.spellingNotLevelled,
                  style: context.text.labelSmall?.copyWith(
                    color: context.colors.onSurface.withValues(alpha: 0.55),
                  ),
                ),
              ],
            ),
          ),
          if (_saving)
            const SizedBox(
              width: 18,
              height: 18,
              child: CircularProgressIndicator(strokeWidth: 2.2),
            )
          // Spelling is measured but not levelled, so there is nothing to
          // choose here (ADR-008).
          else if (widget.level.carriesCefrLevel)
            DropdownButton<CefrLevel>(
              value: widget.level.userSelectedLevel,
              underline: const SizedBox.shrink(),
              borderRadius: AppRadii.fieldBorder,
              items: [
                for (final level in CefrLevel.values)
                  DropdownMenuItem(value: level, child: Text(level.label)),
              ],
              onChanged: (value) => value == null ? null : _update(value),
            ),
        ],
      ),
    );
  }
}

class _DailyTargetCard extends ConsumerStatefulWidget {
  const _DailyTargetCard({required this.level, required this.config});

  final SkillLevel level;
  final PublicConfig config;

  @override
  ConsumerState<_DailyTargetCard> createState() => _DailyTargetCardState();
}

class _DailyTargetCardState extends ConsumerState<_DailyTargetCard> {
  late double _value = widget.level.dailyTargetWords.toDouble();

  Future<void> _commit() async {
    try {
      await ref.read(wordOsApiProvider).updateDailyTarget(
            skill: widget.level.skill,
            target: _value.round(),
          );
      await ref.read(sessionProvider.notifier).refresh();
      ref.invalidate(hubProvider);
    } on ApiException catch (e) {
      if (mounted) {
        ScaffoldMessenger.of(context)
            .showSnackBar(SnackBar(content: Text(e.message)));
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final s = ref.watch(stringsProvider);
    final color = SkillVisuals.color(context, widget.level.skill);
    final min = widget.config.minDailyTarget;
    final max = widget.config.maxDailyTarget;

    return AppCard(
      child: Column(
        children: [
          Row(
            children: [
              Icon(SkillVisuals.icon(widget.level.skill), color: color, size: 20),
              const SizedBox(width: AppSpacing.sm),
              Expanded(
                child: Text(s.skillName(widget.level.skill),
                    style: context.text.titleSmall),
              ),
              LevelBadge(label: '${_value.round()}', color: color),
            ],
          ),
          Slider(
            value: _value.clamp(min.toDouble(), max.toDouble()),
            min: min.toDouble(),
            max: max.toDouble(),
            divisions: max - min,
            label: '${_value.round()}',
            onChanged: (v) => setState(() => _value = v),
            onChangeEnd: (_) => _commit(),
          ),
        ],
      ),
    );
  }
}

/// Interests stay editable for the whole life of the account: tapping a chip
/// toggles it and "Other" adds one that is not in the catalogue at all
/// (demo review §12).
class _InterestsCard extends ConsumerStatefulWidget {
  const _InterestsCard({required this.user});

  final UserProfile user;

  @override
  ConsumerState<_InterestsCard> createState() => _InterestsCardState();
}

class _InterestsCardState extends ConsumerState<_InterestsCard> {
  bool _saving = false;

  Future<void> _save(Set<String> next) async {
    if (next.isEmpty) {
      // The learning content generator needs at least one topic to work with.
      final s = ref.read(stringsProvider);
      ScaffoldMessenger.of(context)
          .showSnackBar(SnackBar(content: Text(s.interestsKeepAtLeastOne)));
      return;
    }

    setState(() => _saving = true);
    try {
      final profile =
          await ref.read(wordOsApiProvider).saveInterests(next.toList());
      ref.read(sessionProvider.notifier).updateUser(profile);
    } on ApiException catch (e) {
      if (mounted) {
        ScaffoldMessenger.of(context)
            .showSnackBar(SnackBar(content: Text(e.message)));
      }
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final options = ref.watch(interestOptionsProvider).valueOrNull ?? const [];

    return AppCard(
      child: Opacity(
        opacity: _saving ? 0.6 : 1,
        child: InterestsEditor(
          options: options,
          selected: widget.user.interests.toSet(),
          enabled: !_saving,
          onChanged: _save,
        ),
      ),
    );
  }
}

class _AppearanceCard extends ConsumerWidget {
  const _AppearanceCard();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    final mode = ref.watch(themeModeProvider);
    final locale = ref.watch(localeProvider);

    return AppCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          SegmentedButton<ThemeMode>(
            segments: [
              ButtonSegment(value: ThemeMode.system, label: Text(s.themeSystem)),
              ButtonSegment(value: ThemeMode.light, label: Text(s.themeLight)),
              ButtonSegment(value: ThemeMode.dark, label: Text(s.themeDark)),
            ],
            selected: {mode},
            showSelectedIcon: false,
            onSelectionChanged: (value) =>
                ref.read(themeModeProvider.notifier).setThemeMode(value.first),
          ),
          const SizedBox(height: AppSpacing.sm),
          SegmentedButton<String>(
            segments: const [
              ButtonSegment(value: 'ar', label: Text('العربية')),
              ButtonSegment(value: 'en', label: Text('English')),
            ],
            selected: {locale.languageCode},
            showSelectedIcon: false,
            onSelectionChanged: (value) => ref
                .read(localeProvider.notifier)
                .setLocale(Locale(value.first)),
          ),
        ],
      ),
    );
  }
}

/// Doorway to the Owner area. It is a plain link — every piece of actual
/// authorization lives on the server (`MockAdmin.requireOwner`).
class _OwnerEntryCard extends ConsumerWidget {
  const _OwnerEntryCard();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    return AppCard(
      onTap: () => context.push(Routes.developer),
      color: context.palette.warningSurface,
      borderColor: context.palette.warning.withValues(alpha: 0.35),
      child: Row(
        children: [
          Icon(Icons.insights_rounded, color: context.palette.warning),
          const SizedBox(width: AppSpacing.sm),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(s.developerDashboard, style: context.text.titleSmall),
                Text(
                  s.devEntryHint,
                  style: context.text.labelSmall?.copyWith(
                    color: context.colors.onSurface.withValues(alpha: 0.6),
                  ),
                ),
              ],
            ),
          ),
          const Icon(Icons.chevron_right_rounded),
        ],
      ),
    );
  }
}
