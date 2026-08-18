import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../app/router.dart';

import '../../core/l10n/app_strings.dart';
import '../../core/theme/app_tokens.dart';
import 'country_code_field.dart';
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
  final _phone = TextEditingController();

  // Yemen first: it is where most current learners are, and a sensible default
  // beats an empty selector.
  CallingCode _country = kCallingCodes.first;

  @override
  void dispose() {
    _name.dispose();
    _email.dispose();
    _password.dispose();
    _phone.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    if (!_formKey.currentState!.validate()) return;
    await ref.read(sessionProvider.notifier).signUp(
          _email.text.trim(),
          _password.text,
          _name.text.trim(),
          phoneCountryCode: _country.dialCode,
          phoneNumber: _phone.text.trim(),
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
              CountryCodeField(
                selected: _country,
                onCountryChanged: (c) => setState(() => _country = c),
                controller: _phone,
                label: s.phoneNumber,
                arabic: s.locale.languageCode == 'ar',
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
                // Popping alone is not enough: a learner who arrived from the
                // product tour has nothing behind them, and the link silently
                // did nothing at all. Going to the route always works, whether
                // this screen was pushed or landed on directly.
                // `go`, not `pop`. Sign-in and register are peers, not a
                // screen and its child: popping only works when register was
                // pushed from login, and did nothing at all for a learner who
                // arrived from the product tour. Replacing the location works
                // from either.
                context.go(Routes.login);
              },
              child: Text(s.signIn),
            ),
          ],
        ),
      ],
    );
  }
}
