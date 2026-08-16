import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/l10n/app_strings.dart';
import '../../core/theme/app_tokens.dart';
import 'auth_scaffold.dart';
import 'session_controller.dart';

class RegisterScreen extends ConsumerStatefulWidget {
  const RegisterScreen({super.key});

  @override
  ConsumerState<RegisterScreen> createState() => _RegisterScreenState();
}

class _RegisterScreenState extends ConsumerState<RegisterScreen> {
  final _formKey = GlobalKey<FormState>();
  final _name = TextEditingController();
  final _email = TextEditingController();
  final _password = TextEditingController();

  @override
  void dispose() {
    _name.dispose();
    _email.dispose();
    _password.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    if (!_formKey.currentState!.validate()) return;
    await ref.read(sessionProvider.notifier).signUp(
          _email.text.trim(),
          _password.text,
          _name.text.trim(),
        );
  }

  @override
  Widget build(BuildContext context) {
    final s = ref.watch(stringsProvider);
    final session = ref.watch(sessionProvider);

    return AuthScaffold(
      title: s.createYourAccount,
      subtitle: s.tagline,
      children: [
        Form(
          key: _formKey,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              TextFormField(
                controller: _name,
                textCapitalization: TextCapitalization.words,
                decoration: InputDecoration(
                  labelText: s.name,
                  prefixIcon: const Icon(Icons.person_outline_rounded),
                ),
              ),
              const SizedBox(height: AppSpacing.sm),
              TextFormField(
                controller: _email,
                keyboardType: TextInputType.emailAddress,
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
          Text(
            session.error!,
            style: context.text.bodySmall
                ?.copyWith(color: context.palette.danger),
          ),
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
              : Text(s.signUp),
        ),
        const SizedBox(height: AppSpacing.sm),
        Wrap(
          alignment: WrapAlignment.center,
          crossAxisAlignment: WrapCrossAlignment.center,
          children: [
            Text(
              s.alreadyHaveAccount,
              style: context.text.bodyMedium?.copyWith(
                color: context.colors.onSurface.withValues(alpha: 0.65),
              ),
            ),
            TextButton(
              onPressed: () {
                ref.read(sessionProvider.notifier).clearError();
                if (context.canPop()) context.pop();
              },
              child: Text(s.signIn),
            ),
          ],
        ),
      ],
    );
  }
}
