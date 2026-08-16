import 'package:flutter/material.dart';

import '../../core/theme/app_tokens.dart';

/// Shared visual frame for the sign-in and sign-up screens.
///
/// The brand mark sits high in the safe area and the form block is optically
/// centred beneath it, rather than the whole column being centred as one lump —
/// that earlier composition left "WordOS" hanging too close to "Welcome back"
/// (demo review §1).
class AuthScaffold extends StatelessWidget {
  const AuthScaffold({
    super.key,
    required this.title,
    required this.subtitle,
    required this.children,
  });

  final String title;
  final String subtitle;
  final List<Widget> children;

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      body: SafeArea(
        child: LayoutBuilder(
          builder: (context, constraints) {
            // On short screens (or with the keyboard up) the brand keeps a
            // smaller headroom so nothing is pushed off-screen.
            final headroom = constraints.maxHeight * 0.06;
            final gap = (constraints.maxHeight * 0.14).clamp(
              AppSpacing.lg,
              96.0,
            );

            return SingleChildScrollView(
              padding: EdgeInsets.fromLTRB(
                AppSpacing.lg,
                headroom.clamp(AppSpacing.sm, AppSpacing.xl),
                AppSpacing.lg,
                AppSpacing.xl,
              ),
              child: ConstrainedBox(
                constraints: const BoxConstraints(maxWidth: 460),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    const _BrandMark(),
                    SizedBox(height: gap),
                    Text(title, style: context.text.headlineMedium),
                    const SizedBox(height: AppSpacing.xs),
                    Text(
                      subtitle,
                      style: context.text.bodyMedium?.copyWith(
                        color: context.colors.onSurface.withValues(alpha: 0.65),
                      ),
                    ),
                    const SizedBox(height: AppSpacing.lg),
                    ...children,
                  ],
                ),
              ),
            );
          },
        ),
      ),
    );
  }
}

class _BrandMark extends StatelessWidget {
  const _BrandMark();

  @override
  Widget build(BuildContext context) {
    return Row(
      children: [
        Container(
          width: 46,
          height: 46,
          decoration: BoxDecoration(
            gradient: LinearGradient(
              colors: [
                context.colors.primary,
                context.palette.skillListening,
              ],
              begin: Alignment.topLeft,
              end: Alignment.bottomRight,
            ),
            borderRadius: const BorderRadius.all(AppRadii.md),
          ),
          alignment: Alignment.center,
          child: const Text(
            'W',
            style: TextStyle(
              color: Colors.white,
              fontSize: 24,
              fontWeight: FontWeight.w800,
            ),
          ),
        ),
        const SizedBox(width: AppSpacing.sm),
        Text('WordOS', style: context.text.titleLarge),
      ],
    );
  }
}
