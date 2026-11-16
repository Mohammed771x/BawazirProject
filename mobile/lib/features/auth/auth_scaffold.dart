import 'package:flutter/material.dart';

import '../../core/theme/app_tokens.dart';
import '../../core/widgets/brand.dart';

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
                    const WordOsBrand(),
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

/// The one error line under an auth form, in the danger colours.
class AuthErrorBanner extends StatelessWidget {
  const AuthErrorBanner({super.key, required this.message});

  final String message;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.all(AppSpacing.sm),
      decoration: BoxDecoration(
        color: context.palette.dangerSurface,
        borderRadius: AppRadii.fieldBorder,
        border: Border.all(color: context.palette.danger.withValues(alpha: 0.4)),
      ),
      child: Row(
        children: [
          Icon(Icons.error_outline_rounded,
              size: 18, color: context.palette.danger),
          const SizedBox(width: AppSpacing.xs),
          Expanded(
            child: Text(
              message,
              style: context.text.bodySmall
                  ?.copyWith(color: context.palette.danger),
            ),
          ),
        ],
      ),
    );
  }
}
