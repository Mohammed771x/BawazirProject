import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../app/router.dart';
import '../../core/api/api_providers.dart';
import '../../core/api/wordos_api.dart';
import '../../core/l10n/app_strings.dart';
import '../../core/theme/app_tokens.dart';
import 'auth_scaffold.dart';

/// Recovering a forgotten password, in two steps on one screen (ADR-078).
///
/// One screen rather than two routes because the second step needs the first
/// step's email, and threading it through navigation is how a learner ends up
/// on a code screen with no address to send the code to — after a back gesture,
/// a deep link, or the app being killed while they read their mail.
///
/// Nothing here decides anything. Whether a code is valid, expired, spent or
/// simply wrong is the server's answer, and this screen shows the one sentence
/// it gives for all four (rule R1).
class ForgotPasswordScreen extends ConsumerStatefulWidget {
  const ForgotPasswordScreen({super.key});

  @override
  ConsumerState<ForgotPasswordScreen> createState() =>
      _ForgotPasswordScreenState();
}

enum _Step { requestCode, enterCode }

class _ForgotPasswordScreenState extends ConsumerState<ForgotPasswordScreen> {
  final _requestKey = GlobalKey<FormState>();
  final _resetKey = GlobalKey<FormState>();

  final _email = TextEditingController();
  final _code = TextEditingController();
  final _password = TextEditingController();
  final _confirm = TextEditingController();

  _Step _step = _Step.requestCode;
  bool _busy = false;
  String? _error;

  @override
  void dispose() {
    _email.dispose();
    _code.dispose();
    _password.dispose();
    _confirm.dispose();
    super.dispose();
  }

  Future<void> _run(Future<void> Function() action) async {
    setState(() {
      _busy = true;
      _error = null;
    });

    final s = ref.read(stringsProvider);
    try {
      await action();
    } on ApiException catch (e) {
      if (mounted) setState(() => _error = s.apiError(e.code, e.message));
    } catch (_) {
      if (mounted) setState(() => _error = s.somethingWentWrong);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _requestCode() async {
    if (!_requestKey.currentState!.validate()) return;

    await _run(() async {
      await ref
          .read(wordOsApiProvider)
          .requestPasswordReset(_email.text.trim());

      if (!mounted) return;
      setState(() => _step = _Step.enterCode);
    });
  }

  Future<void> _submitNewPassword() async {
    if (!_resetKey.currentState!.validate()) return;

    await _run(() async {
      await ref.read(wordOsApiProvider).resetPassword(
            email: _email.text.trim(),
            code: _code.text.trim(),
            newPassword: _password.text,
          );

      if (!mounted) return;

      // Back to sign-in, deliberately: the reset returns no session, so the
      // learner proves they know the password they just chose.
      final s = ref.read(stringsProvider);
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text(s.passwordChangedSignIn)),
      );
      context.go(Routes.login);
    });
  }

  @override
  Widget build(BuildContext context) {
    final s = ref.watch(stringsProvider);

    return AuthScaffold(
      title: s.resetPasswordTitle,
      subtitle: _step == _Step.requestCode
          ? s.resetPasswordIntro
          : s.resetCodeSentIfRegistered,
      children: [
        if (_step == _Step.requestCode) ..._requestStep(s) else ..._codeStep(s),
        if (_error != null) ...[
          const SizedBox(height: AppSpacing.md),
          _ErrorBanner(message: _error!),
        ],
        const SizedBox(height: AppSpacing.lg),
        FilledButton(
          onPressed: _busy
              ? null
              : (_step == _Step.requestCode
                  ? _requestCode
                  : _submitNewPassword),
          child: _busy
              ? const SizedBox(
                  width: 20,
                  height: 20,
                  child: CircularProgressIndicator(
                    strokeWidth: 2.4,
                    color: Colors.white,
                  ),
                )
              : Text(_step == _Step.requestCode ? s.sendCode : s.setNewPassword),
        ),
        const SizedBox(height: AppSpacing.sm),
        if (_step == _Step.enterCode)
          TextButton(
            onPressed: _busy ? null : _requestCode,
            child: Text(s.resendCode),
          ),
        TextButton(
          onPressed: _busy ? null : () => context.go(Routes.login),
          child: Text(s.signIn),
        ),
      ],
    );
  }

  List<Widget> _requestStep(AppStrings s) => [
        Form(
          key: _requestKey,
          child: TextFormField(
            controller: _email,
            keyboardType: TextInputType.emailAddress,
            autofillHints: const [AutofillHints.email],
            decoration: InputDecoration(
              labelText: s.email,
              prefixIcon: const Icon(Icons.alternate_email_rounded),
            ),
            validator: (v) =>
                (v == null || !v.contains('@')) ? s.emailRequired : null,
            onFieldSubmitted: (_) => _requestCode(),
          ),
        ),
      ];

  List<Widget> _codeStep(AppStrings s) => [
        Form(
          key: _resetKey,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              TextFormField(
                controller: _code,
                keyboardType: TextInputType.number,
                autofillHints: const [AutofillHints.oneTimeCode],
                maxLength: 6,
                // Digits only, and never a right-to-left run: a code is a
                // number, and in an Arabic interface an unconstrained field
                // renders it in a direction that makes "014" look like "410".
                textDirection: TextDirection.ltr,
                inputFormatters: [FilteringTextInputFormatter.digitsOnly],
                decoration: InputDecoration(
                  labelText: s.resetCode,
                  prefixIcon: const Icon(Icons.pin_outlined),
                  counterText: '',
                ),
                validator: (v) =>
                    (v == null || v.trim().length != 6) ? s.resetCodeRequired : null,
              ),
              const SizedBox(height: AppSpacing.sm),
              TextFormField(
                controller: _password,
                obscureText: true,
                autofillHints: const [AutofillHints.newPassword],
                decoration: InputDecoration(
                  labelText: s.newPassword,
                  prefixIcon: const Icon(Icons.lock_outline_rounded),
                ),
                validator: (v) =>
                    (v == null || v.length < AppStrings.minPasswordLength)
                        ? s.passwordRequired
                        : null,
              ),
              const SizedBox(height: AppSpacing.sm),
              TextFormField(
                controller: _confirm,
                obscureText: true,
                decoration: InputDecoration(
                  labelText: s.confirmNewPassword,
                  prefixIcon: const Icon(Icons.lock_outline_rounded),
                ),
                // Caught here rather than by the server, which has no way to
                // know: a learner who mistypes a password they cannot see is
                // otherwise locked out by their own reset.
                validator: (v) =>
                    v != _password.text ? s.passwordsDoNotMatch : null,
                onFieldSubmitted: (_) => _submitNewPassword(),
              ),
            ],
          ),
        ),
      ];
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
