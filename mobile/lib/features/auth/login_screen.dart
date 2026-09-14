import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../app/router.dart';
import '../../core/api/api_providers.dart';
import '../../core/l10n/app_strings.dart';
import '../../core/theme/app_tokens.dart';
import 'auth_scaffold.dart';
import 'session_controller.dart';

class LoginScreen extends ConsumerStatefulWidget {
  const LoginScreen({super.key});

  @override
  ConsumerState<LoginScreen> createState() => _LoginScreenState();
}

class _LoginScreenState extends ConsumerState<LoginScreen> {
  final _formKey = GlobalKey<FormState>();

  late final TextEditingController _email;
  late final TextEditingController _password;

  @override
  void initState() {
    super.initState();

    // The seeded demo account exists only in the mock backend. Prefilling it
    // against the real API hands the learner credentials that cannot work, and
    // the failure reads as "the app is broken" rather than "that account is not
    // yours".
    //
    // Read from the provider, not from `AppEnvironment.current`. This was the
    // one place in the app that went round the provider to the compile-time
    // constant, and it could not be overridden: a widget test that pinned the
    // mock still got an empty form here, tapped "Sign in", failed validation
    // and never reached the hub — reported as "Found 0 widgets with text
    // 'Skills Hub'", which is a UI regression that had not happened.
    //
    // In `initState` rather than as a field initializer because `ref` is not
    // available until the state is mounted.
    final isDemoBackend = ref.read(appEnvironmentProvider).useMockBackend;

    _email = TextEditingController(
        text: isDemoBackend ? 'demo@wordos.app' : '');
    _password = TextEditingController(text: isDemoBackend ? 'wordos123' : '');
  }

  @override
  void dispose() {
    _email.dispose();
    _password.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    if (!_formKey.currentState!.validate()) return;
    await ref
        .read(sessionProvider.notifier)
        .signIn(_email.text.trim(), _password.text);
  }

  @override
  Widget build(BuildContext context) {
    final s = ref.watch(stringsProvider);
    final session = ref.watch(sessionProvider);

    return AuthScaffold(
      title: s.welcomeBack,
      subtitle: s.tagline,
      children: [
        Form(
          key: _formKey,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              TextFormField(
                controller: _email,
                keyboardType: TextInputType.emailAddress,
                autofillHints: const [AutofillHints.email],
                decoration: InputDecoration(
                  labelText: s.email,
                  prefixIcon: const Icon(Icons.alternate_email_rounded),
                ),
                validator: (v) =>
                    (v == null || !v.contains('@')) ? s.emailRequired : null,
              ),
              const SizedBox(height: AppSpacing.sm),
              TextFormField(
                controller: _password,
                obscureText: true,
                autofillHints: const [AutofillHints.password],
                decoration: InputDecoration(
                  labelText: s.password,
                  prefixIcon: const Icon(Icons.lock_outline_rounded),
                ),
                validator: (v) =>
                    (v == null || v.length < AppStrings.minPasswordLength)
                        ? s.passwordRequired
                        : null,
                onFieldSubmitted: (_) => _submit(),
              ),
            ],
          ),
        ),
        if (session.error != null) ...[
          const SizedBox(height: AppSpacing.md),
          _ErrorBanner(message: session.error!),
        ],
        const SizedBox(height: AppSpacing.lg),
        FilledButton(
          onPressed: session.busy ? null : _submit,
          child: session.busy
              ? const SizedBox(
                  width: 20,
                  height: 20,
                  child: CircularProgressIndicator(
                    strokeWidth: 2.4,
                    color: Colors.white,
                  ),
                )
              : Text(s.signIn),
        ),
        const SizedBox(height: AppSpacing.xs),
        // Directly under the sign-in button, where a learner looks after being
        // refused — not buried beside "Sign up", which is a different problem
        // (ADR-078).
        Align(
          alignment: AlignmentDirectional.centerEnd,
          child: TextButton(
            onPressed: () {
              ref.read(sessionProvider.notifier).clearError();
              context.go(Routes.forgotPassword);
            },
            child: Text(s.forgotPassword),
          ),
        ),
        const SizedBox(height: AppSpacing.xs),
        Wrap(
          alignment: WrapAlignment.center,
          crossAxisAlignment: WrapCrossAlignment.center,
          children: [
            Text(
              s.noAccountYet,
              style: context.text.bodyMedium?.copyWith(
                color: context.colors.onSurface.withValues(alpha: 0.65),
              ),
            ),
            TextButton(
              onPressed: () {
                ref.read(sessionProvider.notifier).clearError();
                // Peers, not parent and child — see the note on the
                // matching link in register_screen.
                context.go(Routes.register);
              },
              child: Text(s.signUp),
            ),
          ],
        ),
        if (ref.watch(appEnvironmentProvider).useMockBackend) ...[
          const SizedBox(height: AppSpacing.md),
          Container(
            padding: const EdgeInsets.all(AppSpacing.sm),
            decoration: BoxDecoration(
              color: context.palette.subtleSurface,
              borderRadius: AppRadii.fieldBorder,
            ),
            child: Row(
              children: [
                Icon(Icons.science_outlined,
                    size: 18, color: context.colors.primary),
                const SizedBox(width: AppSpacing.xs),
                Expanded(
                  child: Text(s.demoHint, style: context.text.bodySmall),
                ),
              ],
            ),
          ),
        ],
      ],
    );
  }
}

class _ErrorBanner extends StatelessWidget {
  const _ErrorBanner({required this.message});

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

