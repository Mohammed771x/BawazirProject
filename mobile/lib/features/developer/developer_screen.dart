import 'dart:math' as math;
import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:intl/intl.dart';

import '../../app/router.dart';
import '../../core/api/api_providers.dart';
import '../../core/api/wordos_api.dart';
import '../../core/l10n/app_strings.dart';
import '../../core/models/models.dart';
import '../../core/theme/app_tokens.dart';
import '../../core/theme/skill_visuals.dart';
import '../../core/widgets/app_widgets.dart';
import '../hub/hub_screen.dart';
import 'developer_widgets.dart';

/// How far back the dashboard is looking. Null is all time.
///
/// Part 3 asks for Today / 5 days / 10 days / custom, because the two questions
/// the Owner asks are different: "is this working?" is answered over months,
/// "did something break today?" over hours. One shared window keeps the
/// overview and the learner list talking about the same period.
final adminWindowProvider = StateProvider<int?>((ref) => null);

/// What the Owner has typed into the learner search.
final adminSearchProvider = StateProvider<String>((ref) => '');

final adminOverviewProvider = FutureProvider.autoDispose<AdminOverview>(
  (ref) => ref
      .watch(wordOsApiProvider)
      .adminOverview(days: ref.watch(adminWindowProvider)),
);

final adminUsersProvider = FutureProvider.autoDispose<AdminUserPage>(
  (ref) => ref.watch(wordOsApiProvider).adminUsers(
        query: ref.watch(adminSearchProvider),
        days: ref.watch(adminWindowProvider),
      ),
);

/// What learners have written to the Owner (ADR-053).
///
/// Unfiltered: unread first, newest first, which is the order the screen exists
/// to show. A filter would hide the thing the tab is for.
final adminFeedbackProvider = FutureProvider.autoDispose<FeedbackPage>(
  (ref) => ref.watch(wordOsApiProvider).adminFeedback(),
);

/// The Owner area: **not** part of the learner's Settings and not reachable by
/// a normal account. The router keeps non-owners out of the route, and the API
/// refuses the calls regardless of what the client does (`MockAdmin`).
class DeveloperScreen extends ConsumerStatefulWidget {
  const DeveloperScreen({super.key});

  @override
  ConsumerState<DeveloperScreen> createState() => _DeveloperScreenState();
}

class _DeveloperScreenState extends ConsumerState<DeveloperScreen>
    with SingleTickerProviderStateMixin {
  late final TabController _tabs = TabController(length: 4, vsync: this);

  @override
  void dispose() {
    _tabs.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final s = ref.watch(stringsProvider);

    return Scaffold(
      appBar: AppBar(
        title: Text(s.developerDashboard),
        bottom: TabBar(
          controller: _tabs,
          tabs: [
            Tab(text: s.devOverview),
            Tab(text: s.devUsers),
            Tab(text: s.devFeedback),
            Tab(text: s.developerTools),
          ],
        ),
      ),
      body: TabBarView(
        controller: _tabs,
        children: const [
          _OverviewTab(),
          _UsersTab(),
          _FeedbackTab(),
          _ToolsTab(),
        ],
      ),
    );
  }
}

/// The reporting window, shared by the overview and the learner list.
///
/// A row of chips rather than a dropdown: the three the Owner actually uses are
/// one tap away, and "custom" is there for the fourth question nobody
/// anticipated.
class _RangeSelector extends ConsumerWidget {
  const _RangeSelector();

  static const _presets = [null, 1, 5, 10];

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    final selected = ref.watch(adminWindowProvider);

    Widget chip(String label, int? days) => Padding(
          padding: const EdgeInsets.only(right: AppSpacing.xs),
          child: ChoiceChip(
            label: Text(label),
            selected: selected == days,
            onSelected: (_) =>
                ref.read(adminWindowProvider.notifier).state = days,
          ),
        );

    return SingleChildScrollView(
      scrollDirection: Axis.horizontal,
      child: Row(
        children: [
          chip(s.devRangeAllTime, null),
          chip(s.devRangeToday, 1),
          chip(s.devRangeDays(5), 5),
          chip(s.devRangeDays(10), 10),
          Padding(
            padding: const EdgeInsets.only(right: AppSpacing.xs),
            child: ActionChip(
              avatar: const Icon(Icons.tune_rounded, size: 16),
              label: Text(
                selected != null && !_presets.contains(selected)
                    ? s.devRangeDays(selected)
                    : s.devRangeCustom,
              ),
              onPressed: () => _askForRange(context, ref, s),
            ),
          ),
        ],
      ),
    );
  }

  Future<void> _askForRange(
    BuildContext context,
    WidgetRef ref,
    AppStrings s,
  ) async {
    final days = await showDialog<int>(
      context: context,
      builder: (_) => _CustomRangeDialog(strings: s),
    );

    // A nonsense number leaves the window as it was rather than reporting on
    // zero days. The dialog has already refused what it could; this is the
    // second gate, because the number also reaches date arithmetic.
    if (days != null && days > 0 && context.mounted) {
      ref.read(adminWindowProvider.notifier).state = days;
    }
  }
}

/// Asks for a number of days.
///
/// A widget of its own, rather than an `AlertDialog` built inline, because the
/// text controller has to outlive the dialog's *exit animation*. Creating it in
/// the caller and disposing it as soon as `showDialog` returned meant the field
/// was still on screen, still animating, still reading a controller that had
/// just been thrown away — which crashed the app on the frame after the Owner
/// pressed Save. That is why picking a custom range closed the app while the
/// preset chips beside it were fine.
class _CustomRangeDialog extends StatefulWidget {
  const _CustomRangeDialog({required this.strings});

  final AppStrings strings;

  @override
  State<_CustomRangeDialog> createState() => _CustomRangeDialogState();
}

class _CustomRangeDialogState extends State<_CustomRangeDialog> {
  final _controller = TextEditingController();

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  /// What the field currently holds, or null if it is not a usable window.
  ///
  /// Anything a numeric keyboard can produce arrives here: nothing, a minus
  /// sign, a decimal point, or more digits than an integer holds.
  int? get _days {
    final parsed = int.tryParse(_controller.text.trim());
    if (parsed == null || parsed <= 0) return null;

    // Longer than the product has existed is the same question as "all time",
    // and the server clamps to the same ceiling.
    return math.min(parsed, _maxDays);
  }

  static const int _maxDays = 3650;

  @override
  Widget build(BuildContext context) {
    final s = widget.strings;

    return AlertDialog(
      title: Text(s.devRangeCustomTitle),
      content: TextField(
        controller: _controller,
        keyboardType: TextInputType.number,
        autofocus: true,
        // Digits only: a minus or a decimal point can only ever produce a
        // window that has to be rejected afterwards.
        inputFormatters: [FilteringTextInputFormatter.digitsOnly],
        maxLength: 6,
        onChanged: (_) => setState(() {}),
        onSubmitted: (_) => Navigator.of(context).pop(_days),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: Text(s.cancel),
        ),
        FilledButton(
          // Disabled rather than silently ignored, so it is clear that the
          // field wants a number and has not got one.
          onPressed: _days == null ? null : () => Navigator.of(context).pop(_days),
          child: Text(s.save),
        ),
      ],
    );
  }
}

class _OverviewTab extends ConsumerWidget {
  const _OverviewTab();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    final overview = ref.watch(adminOverviewProvider);

    return overview.when(
      loading: () => BusyView(message: s.loading),
      error: (e, _) => ErrorView(
        message: e is ApiException ? e.message : s.somethingWentWrong,
        retryLabel: s.retry,
        onRetry: () => ref.invalidate(adminOverviewProvider),
      ),
      data: (data) => ListView(
        padding: const EdgeInsets.all(AppSpacing.md),
        children: [
          const _RangeSelector(),
          const SizedBox(height: AppSpacing.sm),
          GridView.count(
            crossAxisCount: 2,
            shrinkWrap: true,
            physics: const NeverScrollableScrollPhysics(),
            childAspectRatio: 1.7,
            crossAxisSpacing: AppSpacing.xs,
            mainAxisSpacing: AppSpacing.xs,
            children: [
              MetricTile(label: s.devUserCount, value: '${data.userCount}'),
              MetricTile(
                label: s.devActiveToday,
                value: '${data.activeToday}',
                caption: s.devActiveThisWeek(data.activeThisWeek),
              ),
              MetricTile(
                label: s.devAvgWordsPerDay,
                value: data.averageWordsPerUserPerDay.toStringAsFixed(1),
                caption: s.devWordsTotal(data.wordsAddedTotal),
              ),
              MetricTile(
                label: s.devAvgSessions,
                value: data.averageSessionsPerUser.toStringAsFixed(1),
                caption: s.devTypicalDuration(
                  (data.medianSessionDurationMs / 1000).round(),
                ),
              ),
              MetricTile(
                label: s.devPipelineCompletion,
                value: '${(data.pipelineCompletionRate * 100).round()}%',
                caption: s.devPipelineCompletionHint,
              ),
              MetricTile(
                label: s.devAiFallback,
                value: '${(data.aiFallbackRate * 100).round()}%',
                tone: data.aiFallbackRate > 0.1
                    ? context.palette.warning
                    : context.palette.success,
                caption: s.devAiFallbackHint,
              ),
            ],
          ),
          const SizedBox(height: AppSpacing.lg),

          SectionHeader(
            title: s.devPassRate,
            subtitle: s.devPassRateHint,
          ),
          AppCard(
            child: BarChart(
              bars: [
                for (final stat in data.skillStats)
                  ChartBar(
                    label: s.skillName(stat.skill),
                    value: stat.passRate,
                    color: SkillVisuals.color(context, stat.skill),
                  ),
              ],
            ),
          ),
          const SizedBox(height: AppSpacing.lg),

          SectionHeader(
            title: s.devFirstAttempt,
            subtitle: s.devFirstAttemptHint,
          ),
          AppCard(
            child: BarChart(
              bars: [
                for (final stat in data.skillStats)
                  ChartBar(
                    label: s.skillName(stat.skill),
                    value: stat.firstAttemptAccuracy,
                    color: SkillVisuals.color(context, stat.skill),
                  ),
              ],
            ),
          ),
          const SizedBox(height: AppSpacing.lg),

          SectionHeader(
            title: s.devFailureDistribution,
            subtitle: s.devFailureDistributionHint,
          ),
          AppCard(
            child: BarChart(
              bars: [
                for (final entry in data.failureDistribution.entries)
                  ChartBar(
                    label: s.skillName(entry.key),
                    value: entry.value,
                    color: context.palette.danger,
                  ),
              ],
            ),
          ),
          const SizedBox(height: AppSpacing.lg),

          SectionHeader(
            title: s.devLevelDistribution,
            subtitle: s.devLevelDistributionHint,
          ),
          for (final distribution in data.levelDistributions) ...[
            AppCard(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    s.skillName(distribution.skill),
                    style: context.text.titleSmall,
                  ),
                  const SizedBox(height: AppSpacing.xs),
                  ColumnChart(
                    columns: [
                      for (final level in CefrLevel.values)
                        ChartColumn(
                          label: level.label,
                          value: (distribution.counts[level] ?? 0).toDouble(),
                          color:
                              SkillVisuals.color(context, distribution.skill),
                        ),
                    ],
                  ),
                ],
              ),
            ),
            const SizedBox(height: AppSpacing.xs),
          ],
          const SizedBox(height: AppSpacing.lg),

          SectionHeader(
            title: s.devTopInterests,
            subtitle: s.devTopInterestsHint,
          ),
          AppCard(
            child: BarChart(
              valueLabel: (v) => v.toStringAsFixed(0),
              bars: [
                for (final interest in data.topInterests.take(10))
                  ChartBar(
                    // Custom interests are marked, because "which topics did
                    // learners have to type themselves" is the signal for
                    // growing the catalogue.
                    label: interest.isCustom
                        ? '✨ ${interest.interest}'
                        : interest.interest,
                    value: interest.userCount.toDouble(),
                    color: interest.isCustom
                        ? context.palette.warning
                        : context.colors.primary,
                  ),
              ],
            ),
          ),
          const SizedBox(height: AppSpacing.xxl),
        ],
      ),
    );
  }
}

class _UsersTab extends ConsumerStatefulWidget {
  const _UsersTab();

  @override
  ConsumerState<_UsersTab> createState() => _UsersTabState();
}

class _UsersTabState extends ConsumerState<_UsersTab> {
  final _search = TextEditingController();
  Timer? _debounce;

  @override
  void dispose() {
    _debounce?.cancel();
    _search.dispose();
    super.dispose();
  }

  void _onSearchChanged(String value) {
    // Debounced: the search runs in PostgreSQL across every learner, and a
    // query per keystroke would be a self-inflicted load test.
    _debounce?.cancel();
    _debounce = Timer(const Duration(milliseconds: 300), () {
      if (mounted) {
        ref.read(adminSearchProvider.notifier).state = value.trim();
      }
    });
  }

  @override
  Widget build(BuildContext context) {
    final s = ref.watch(stringsProvider);
    final users = ref.watch(adminUsersProvider);

    return Column(
      children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(
              AppSpacing.md, AppSpacing.sm, AppSpacing.md, AppSpacing.xs),
          child: TextField(
            controller: _search,
            onChanged: _onSearchChanged,
            decoration: InputDecoration(
              hintText: s.devSearchUsers,
              prefixIcon: const Icon(Icons.search_rounded),
            ),
          ),
        ),
        const Padding(
          padding: EdgeInsets.symmetric(horizontal: AppSpacing.md),
          child: _RangeSelector(),
        ),
        Expanded(child: _list(s, users)),
      ],
    );
  }

  Widget _list(AppStrings s, AsyncValue<AdminUserPage> users) {
    return users.when(
      loading: () => BusyView(message: s.loading),
      error: (e, _) => ErrorView(
        message: e is ApiException ? e.message : s.somethingWentWrong,
        retryLabel: s.retry,
        onRetry: () => ref.invalidate(adminUsersProvider),
      ),
      data: (page) {
        if (page.items.isEmpty) {
          return EmptyState(
            icon: Icons.search_off_rounded,
            title: s.devNoUsersMatch,
          );
        }

        return ListView.separated(
          padding: const EdgeInsets.all(AppSpacing.md),
          // One extra row for the count, which answers "how many match?" —
          // a question the page itself cannot (Part 3 §37).
          itemCount: page.items.length + 1,
          separatorBuilder: (_, _) => const SizedBox(height: AppSpacing.xs),
          itemBuilder: (context, index) {
            if (index == 0) {
              return Padding(
                padding: const EdgeInsets.only(bottom: AppSpacing.xxs),
                child: Text(
                  s.devUsersFound(page.total),
                  style: context.text.labelMedium?.copyWith(
                    color: context.colors.onSurface.withValues(alpha: 0.6),
                  ),
                ),
              );
            }
            final user = page.items[index - 1];
            return AppCard(
            onTap: () => context.push(Routes.developerUser(user.id)),
            child: Row(
              children: [
                CircleAvatar(
                  radius: 20,
                  backgroundColor:
                      context.colors.primary.withValues(alpha: 0.12),
                  child: Text(
                    user.displayName.trim().isEmpty
                        ? '?'
                        : user.displayName.trim()[0].toUpperCase(),
                    style: context.text.titleSmall
                        ?.copyWith(color: context.colors.primary),
                  ),
                ),
                const SizedBox(width: AppSpacing.sm),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Row(
                        children: [
                          Flexible(
                            child: Text(
                              user.displayName,
                              style: context.text.titleSmall,
                              overflow: TextOverflow.ellipsis,
                            ),
                          ),
                          if (user.role == UserRole.owner) ...[
                            const SizedBox(width: AppSpacing.xs),
                            StatusPill(
                              label: s.devOwnerRole,
                              color: context.palette.warning,
                            ),
                          ],
                        ],
                      ),
                      const SizedBox(height: 2),
                      Text(
                        s.devUserRowSummary(
                          user.wordsTotal,
                          user.wordsActive,
                          user.sessionsCompleted,
                        ),
                        style: context.text.labelSmall?.copyWith(
                          color:
                              context.colors.onSurface.withValues(alpha: 0.6),
                        ),
                      ),
                      Text(
                        user.lastActiveAt == null
                            ? s.devNeverActive
                            : s.devLastActive(
                                DateFormat.MMMd(s.locale.languageCode)
                                    .format(user.lastActiveAt!.toLocal()),
                              ),
                        style: context.text.labelSmall?.copyWith(
                          color:
                              context.colors.onSurface.withValues(alpha: 0.45),
                        ),
                      ),
                    ],
                  ),
                ),
                const Icon(Icons.chevron_right_rounded),
              ],
            ),
          );
          },
        );
      },
    );
  }
}

/// Mock-only instruments. They live here rather than in the learner's Settings
/// so a normal account never sees them (demo review §13).
class _ToolsTab extends ConsumerWidget {
  const _ToolsTab();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    final mock = ref.watch(mockApiProvider);

    if (mock == null) {
      return EmptyState(
        icon: Icons.build_outlined,
        title: s.devToolsUnavailable,
        message: s.devToolsUnavailableBody,
      );
    }

    final offsetDays = mock.engine.clockOffset.inDays;

    return ListView(
      padding: const EdgeInsets.all(AppSpacing.md),
      children: [
        SectionHeader(title: s.timeTravel, subtitle: s.timeTravelExplainer),
        AppCard(
          color: context.palette.warningSurface,
          borderColor: context.palette.warning.withValues(alpha: 0.35),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Row(
                children: [
                  Icon(Icons.schedule_rounded,
                      color: context.palette.warning, size: 20),
                  const SizedBox(width: AppSpacing.xs),
                  Expanded(
                    child: Text(
                      s.clockOffsetLabel(offsetDays),
                      style: context.text.titleSmall,
                    ),
                  ),
                ],
              ),
              const SizedBox(height: AppSpacing.sm),
              Row(
                children: [
                  Expanded(
                    child: OutlinedButton(
                      onPressed: () {
                        mock.engine.advanceClock(const Duration(days: 2));
                        ref
                          ..invalidate(hubProvider)
                          ..invalidate(adminOverviewProvider)
                          ..invalidate(adminUsersProvider);
                      },
                      child: Text(s.advanceTwoDays),
                    ),
                  ),
                  const SizedBox(width: AppSpacing.xs),
                  Expanded(
                    child: TextButton(
                      onPressed: () {
                        mock.engine.resetClock();
                        ref
                          ..invalidate(hubProvider)
                          ..invalidate(adminOverviewProvider)
                          ..invalidate(adminUsersProvider);
                      },
                      child: Text(s.resetClock),
                    ),
                  ),
                ],
              ),
            ],
          ),
        ),
      ],
    );
  }
}

/// The Owner's inbox (ADR-053).
///
/// Each message carries who wrote it and how to reach them, because the next
/// thing an Owner does after reading a bug report is answer the person — and
/// the number is the one the learner gave at registration, not one collected
/// for this.
class _FeedbackTab extends ConsumerWidget {
  const _FeedbackTab();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    final feedback = ref.watch(adminFeedbackProvider);

    return feedback.when(
      loading: () => BusyView(message: s.loading),
      error: (e, _) => ErrorView(
        message: e is ApiException ? e.message : s.somethingWentWrong,
        retryLabel: s.retry,
        onRetry: () => ref.invalidate(adminFeedbackProvider),
      ),
      data: (page) {
        if (page.items.isEmpty) {
          return EmptyState(
            icon: Icons.forum_outlined,
            title: s.devFeedbackEmpty,
          );
        }

        return ListView.separated(
          padding: const EdgeInsets.all(AppSpacing.md),
          itemCount: page.items.length + 1,
          separatorBuilder: (_, _) => const SizedBox(height: AppSpacing.xs),
          itemBuilder: (context, index) {
            if (index == 0) {
              return Padding(
                padding: const EdgeInsets.only(bottom: AppSpacing.xs),
                child: SectionHeader(
                  title: s.devFeedbackTitle,
                  subtitle: page.unread > 0
                      ? s.devFeedbackUnread(page.unread)
                      : s.devFeedbackHint,
                ),
              );
            }

            return _FeedbackCard(message: page.items[index - 1]);
          },
        );
      },
    );
  }
}

class _FeedbackCard extends ConsumerWidget {
  const _FeedbackCard({required this.message});

  final FeedbackMessage message;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    final handled = message.handled;

    return AppCard(
      // Handled messages stay visible and fade rather than disappearing: a
      // list that empties as it is read gives the Owner no way back to
      // something they marked by mistake.
      color: handled ? null : context.palette.warningSurface,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Expanded(
                child: Text(
                  message.senderName.isEmpty
                      ? message.senderEmail
                      : message.senderName,
                  style: context.text.titleSmall,
                ),
              ),
              if (handled)
                StatusPill(
                  label: s.devFeedbackHandled,
                  color: context.palette.success,
                ),
            ],
          ),
          const SizedBox(height: AppSpacing.xxs),

          // Their words, as text. Never interpreted: no markup, no links, no
          // HTML anywhere on this path.
          Text(message.body, style: context.text.bodyMedium),

          const SizedBox(height: AppSpacing.xs),
          Wrap(
            spacing: AppSpacing.sm,
            runSpacing: AppSpacing.xxs,
            crossAxisAlignment: WrapCrossAlignment.center,
            children: [
              _Contact(label: message.senderEmail, icon: Icons.mail_outline),
              if (message.senderPhone != null)
                _Contact(
                    label: message.senderPhone!,
                    icon: Icons.phone_outlined),
              Text(
                _stamp(message.createdAt, message.platform, message.appVersion),
                style: context.text.labelSmall?.copyWith(
                  color: context.colors.onSurface.withValues(alpha: 0.55),
                ),
              ),
            ],
          ),
          const SizedBox(height: AppSpacing.xxs),
          Align(
            alignment: AlignmentDirectional.centerEnd,
            child: TextButton(
              onPressed: () async {
                await ref
                    .read(wordOsApiProvider)
                    .adminSetFeedbackHandled(message.id, !handled);
                ref.invalidate(adminFeedbackProvider);
              },
              child: Text(handled
                  ? s.devFeedbackMarkNew
                  : s.devFeedbackMarkHandled),
            ),
          ),
        ],
      ),
    );
  }

  /// When it arrived, and what it arrived from — "it crashed" is twice as
  /// useful with a build beside it.
  static String _stamp(DateTime at, String? platform, String? version) {
    final when = at.toLocal().toString().split('.').first;
    final build = [platform, version].whereType<String>().join(' ');
    return build.isEmpty ? when : '$when · $build';
  }
}

/// One tappable contact detail: tap to copy, because the Owner's next step is
/// pasting it into a phone book or a group.
class _Contact extends ConsumerWidget {
  const _Contact({required this.label, required this.icon});

  final String label;
  final IconData icon;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);

    return InkWell(
      onTap: () async {
        await Clipboard.setData(ClipboardData(text: label));
        if (context.mounted) {
          ScaffoldMessenger.of(context)
              .showSnackBar(SnackBar(content: Text(s.devCopied)));
        }
      },
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(icon, size: 14, color: context.colors.onSurface.withValues(alpha: 0.55)),
          const SizedBox(width: 4),
          Text(
            label,
            style: context.text.labelSmall?.copyWith(
              color: context.colors.onSurface.withValues(alpha: 0.75),
            ),
          ),
        ],
      ),
    );
  }
}
