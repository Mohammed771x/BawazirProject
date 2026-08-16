import 'package:flutter/material.dart';
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

final adminOverviewProvider = FutureProvider.autoDispose<AdminOverview>(
  (ref) => ref.watch(wordOsApiProvider).adminOverview(),
);

final adminUsersProvider = FutureProvider.autoDispose<List<AdminUserSummary>>(
  (ref) => ref.watch(wordOsApiProvider).adminUsers(),
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
  late final TabController _tabs = TabController(length: 3, vsync: this);

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
            Tab(text: s.developerTools),
          ],
        ),
      ),
      body: TabBarView(
        controller: _tabs,
        children: const [
          _OverviewTab(),
          _UsersTab(),
          _ToolsTab(),
        ],
      ),
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
                caption: s.devAvgDuration(
                  (data.averageSessionDurationMs / 1000).round(),
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

class _UsersTab extends ConsumerWidget {
  const _UsersTab();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    final users = ref.watch(adminUsersProvider);

    return users.when(
      loading: () => BusyView(message: s.loading),
      error: (e, _) => ErrorView(
        message: e is ApiException ? e.message : s.somethingWentWrong,
        retryLabel: s.retry,
        onRetry: () => ref.invalidate(adminUsersProvider),
      ),
      data: (list) => ListView.separated(
        padding: const EdgeInsets.all(AppSpacing.md),
        itemCount: list.length,
        separatorBuilder: (_, _) => const SizedBox(height: AppSpacing.xs),
        itemBuilder: (context, index) {
          final user = list[index];
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
      ),
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
