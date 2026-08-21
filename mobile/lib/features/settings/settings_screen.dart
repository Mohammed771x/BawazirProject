import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../app/router.dart';

import '../../core/api/api_providers.dart';
import '../../core/api/wordos_api.dart';
import '../../core/l10n/app_strings.dart';
import '../../core/models/models.dart';
import '../../core/storage/preferences_providers.dart';
import '../../core/support/support_contact.dart';
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
      appBar: AppBar(
        title: Text(s.settings),
        actions: [
          // Sign out lives here as well as at the foot of the list. At the
          // foot alone it sat behind levels, daily targets, interests and
          // appearance — present, but not findable, which for a learner who
          // wants to switch accounts is the same as missing.
          IconButton(
            onPressed: () => _confirmSignOut(context, ref, s),
            icon: const Icon(Icons.logout_rounded),
            tooltip: s.signOut,
          ),
        ],
      ),
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
          // How a learner reaches a person (ADR-053). Before this there was no
          // way at all — no address, no support screen — so someone who hit a
          // problem could only stop using the app, and nobody would learn why.
          SectionHeader(title: s.feedbackSection),
          const _FeedbackCard(),
          const SizedBox(height: AppSpacing.xs),
          // Straight to a person on WhatsApp (ADR-055). Beside the message box
          // rather than instead of it: one is for something that can wait and
          // be read later, the other for a learner who is stuck right now.
          const _SupportCard(),
          const SizedBox(height: AppSpacing.lg),
          // Owner-only doorway. A normal account never renders this, and the
          // route guard plus the API's role check both refuse it anyway
          // (demo review §13).
          if (user.role == UserRole.owner) ...[
            _OwnerEntryCard(),
            const SizedBox(height: AppSpacing.lg),
          ],
          OutlinedButton.icon(
            onPressed: () => _confirmSignOut(context, ref, s),
            icon: const Icon(Icons.logout_rounded),
            label: Text(s.signOut),
          ),
        ],
      ),
    );
  }
}

/// Confirms before ending the session.
///
/// Signing out is one tap from a toolbar now, so it needs the question — an
/// accidental tap otherwise costs the learner their place and a re-login.
Future<void> _confirmSignOut(
  BuildContext context,
  WidgetRef ref,
  AppStrings s,
) async {
  final confirmed = await showDialog<bool>(
    context: context,
    builder: (dialogContext) => AlertDialog(
      title: Text(s.signOut),
      content: Text(s.signOutConfirm),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(dialogContext).pop(false),
          child: Text(s.cancel),
        ),
        FilledButton(
          onPressed: () => Navigator.of(dialogContext).pop(true),
          child: Text(s.signOut),
        ),
      ],
    ),
  );

  if (confirmed == true) {
    await ref.read(sessionProvider.notifier).signOut();
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
            .showSnackBar(SnackBar(content: Text(ref.read(stringsProvider).apiError(e.code, e.message))));
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
            .showSnackBar(SnackBar(content: Text(ref.read(stringsProvider).apiError(e.code, e.message))));
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
            .showSnackBar(SnackBar(content: Text(ref.read(stringsProvider).apiError(e.code, e.message))));
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

/// "Tell us what went wrong" — the learner's half of feedback (ADR-053).
///
/// A dialog rather than a screen: the whole interaction is one field and one
/// button, and a route would put a back stack between a learner and the thing
/// they were about to describe.
class _FeedbackCard extends ConsumerWidget {
  const _FeedbackCard();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);

    return AppCard(
      onTap: () => _composeFeedback(context, ref),
      child: Row(
        children: [
          Icon(Icons.forum_rounded, color: context.colors.primary),
          const SizedBox(width: AppSpacing.sm),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(s.feedbackTitle, style: context.text.titleSmall),
                Text(
                  s.feedbackHint,
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

Future<void> _composeFeedback(BuildContext context, WidgetRef ref) async {
  final sent = await showDialog<bool>(
    context: context,
    builder: (_) => const _FeedbackDialog(),
  );

  if (sent == true && context.mounted) {
    final s = ref.read(stringsProvider);
    ScaffoldMessenger.of(context)
        .showSnackBar(SnackBar(content: Text(s.feedbackSent)));
  }
}

/// Stateful because the controller has to outlive the frames the dialog spends
/// animating away — a disposed controller read during that is what closed the
/// app the last time a dialog owned one (ADR-036).
class _FeedbackDialog extends ConsumerStatefulWidget {
  const _FeedbackDialog();

  @override
  ConsumerState<_FeedbackDialog> createState() => _FeedbackDialogState();
}

class _FeedbackDialogState extends ConsumerState<_FeedbackDialog> {
  final _controller = TextEditingController();
  bool _sending = false;
  String? _error;

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  Future<void> _send() async {
    final body = _controller.text.trim();
    if (body.isEmpty) return;

    setState(() {
      _sending = true;
      _error = null;
    });

    try {
      await ref.read(wordOsApiProvider).sendFeedback(body);
      if (mounted) Navigator.of(context).pop(true);
    } on ApiException catch (e) {
      if (mounted) {
        setState(() {
          _sending = false;
          // The server's own sentence, localised by its code where the app
          // knows it (ADR-035).
          _error = ref.read(stringsProvider).apiError(e.code, e.message);
        });
      }
    } catch (_) {
      if (mounted) {
        setState(() {
          _sending = false;
          _error = ref.read(stringsProvider).feedbackFailed;
        });
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final s = ref.watch(stringsProvider);

    return AlertDialog(
      title: Text(s.feedbackTitle),
      content: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            s.feedbackPrompt,
            style: context.text.bodySmall?.copyWith(
              color: context.colors.onSurface.withValues(alpha: 0.7),
            ),
          ),
          const SizedBox(height: AppSpacing.sm),
          TextField(
            controller: _controller,
            autofocus: true,
            minLines: 4,
            maxLines: 8,
            // The same ceiling the server enforces, so a long message is
            // stopped where it is written rather than rejected after sending.
            maxLength: 4000,
            textInputAction: TextInputAction.newline,
            decoration: InputDecoration(
              hintText: s.feedbackPlaceholder,
              border: const OutlineInputBorder(),
            ),
          ),
          if (_error != null) ...[
            const SizedBox(height: AppSpacing.xs),
            Text(
              _error!,
              style: context.text.labelSmall
                  ?.copyWith(color: context.palette.danger),
            ),
          ],
        ],
      ),
      actions: [
        TextButton(
          onPressed:
              _sending ? null : () => Navigator.of(context).pop(false),
          child: Text(s.cancel),
        ),
        FilledButton(
          onPressed: _sending ? null : _send,
          child: Text(_sending ? s.feedbackSending : s.feedbackSend),
        ),
      ],
    );
  }
}

/// "Contact the developer" — one tap into WhatsApp (ADR-055).
///
/// No dialog and no confirmation: the learner asked to talk to someone, and a
/// question in between is a question they did not ask for.
class _SupportCard extends ConsumerWidget {
  const _SupportCard();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);

    return AppCard(
      onTap: () => _openSupport(context, ref),
      child: Row(
        children: [
          Icon(Icons.support_agent_rounded, color: context.palette.success),
          const SizedBox(width: AppSpacing.sm),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(s.supportTitle, style: context.text.titleSmall),
                Text(
                  s.supportHint,
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

Future<void> _openSupport(BuildContext context, WidgetRef ref) async {
  final opened = await SupportContact.openWhatsApp();
  if (opened || !context.mounted) return;

  // WhatsApp is not installed, or the device refused to hand the link on. The
  // learner still gets the number — a button that does nothing is worse than
  // no button, and copying it is the thing they were about to do anyway.
  final s = ref.read(stringsProvider);

  await showDialog<void>(
    context: context,
    builder: (dialogContext) => AlertDialog(
      title: Text(s.supportTitle),
      content: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(s.supportUnavailable),
          const SizedBox(height: AppSpacing.sm),
          SelectableText(
            SupportContact.displayNumber,
            // The number is a Latin string in an Arabic interface; without
            // this it inherits the page direction and reads back to front.
            textDirection: TextDirection.ltr,
            style: Theme.of(dialogContext).textTheme.titleMedium,
          ),
        ],
      ),
      actions: [
        TextButton(
          onPressed: () async {
            await Clipboard.setData(
                const ClipboardData(text: SupportContact.displayNumber));
            if (dialogContext.mounted) Navigator.of(dialogContext).pop();
          },
          child: Text(s.copy),
        ),
        FilledButton(
          onPressed: () => Navigator.of(dialogContext).pop(),
          child: Text(s.close),
        ),
      ],
    ),
  );
}
