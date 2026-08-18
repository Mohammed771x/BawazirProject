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
  // The seeded demo account exists only in the mock backend. Prefilling it
  // against the real API hands the learner credentials that cannot work, and
  // the failure reads as "the app is broken" rather than "that account is not
  // yours".
  static bool get _isDemoBackend => AppEnvironment.current.useMockBackend;

  final _email =
      TextEditingController(text: _isDemoBackend ? 'demo@wordos.app' : '');
  final _password =
      TextEditingController(text: _isDemoBackend ? 'wordos123' : '');

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
                    (v == null || v.length < 6) ? s.passwordRequired : null,
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
        const SizedBox(height: AppSpacing.sm),
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
        if (_isDemoBackend) ...[
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
