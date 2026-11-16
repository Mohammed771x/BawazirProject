import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_providers.dart';
import '../../core/api/wordos_api.dart';
import '../../core/l10n/app_strings.dart';
import '../../core/theme/app_tokens.dart';
import 'auth_scaffold.dart';
import 'session_controller.dart';

/// The six-digit code a new account is emailed (ADR-141).
///
/// Reached only because the server put the account at `VERIFY_EMAIL`; the
/// router sends it here and nowhere else until the server says otherwise.
/// Whether a code is right, expired or spent is the server's answer, shown as
/// it gives it (rule R1). Verifying returns the profile, and the stage in it
/// is what moves the learner on to interests.
class VerifyEmailScreen extends ConsumerStatefulWidget {
  const VerifyEmailScreen({super.key});

  @override
  ConsumerState<VerifyEmailScreen> createState() => _VerifyEmailScreenState();
}

class _VerifyEmailScreenState extends ConsumerState<VerifyEmailScreen> {
  final _code = TextEditingController();
  bool _busy = false;
  String? _error;

  /// Seconds until "send another code" is offered. Starts full: a code was
  /// sent when the account was made, a moment ago.
  int _cooldown = 60;
  Timer? _ticker;

  @override
  void initState() {
    super.initState();
    _startCooldown(60);
  }

  @override
  void dispose() {
    _ticker?.cancel();
    _code.dispose();
    super.dispose();
  }

  void _startCooldown(int seconds) {
    _ticker?.cancel();
    setState(() => _cooldown = seconds);
    _ticker = Timer.periodic(const Duration(seconds: 1), (t) {
      if (!mounted) return t.cancel();
      setState(() => _cooldown = _cooldown > 0 ? _cooldown - 1 : 0);
      if (_cooldown == 0) t.cancel();
    });
  }

  Future<void> _verify() async {
    final s = ref.read(stringsProvider);
    final code = _code.text.trim();
    if (code.length != 6) {
      setState(() => _error = s.resetCodeRequired);
      return;
    }
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final profile = await ref.read(wordOsApiProvider).verifyEmail(code);
      // The router follows the stage in this profile to interests.
      ref.read(sessionProvider.notifier).updateUser(profile);
    } on ApiException catch (e) {
      if (mounted) setState(() => _error = s.apiError(e.code, e.message));
    } catch (_) {
      if (mounted) setState(() => _error = s.somethingWentWrong);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _resend() async {
    final s = ref.read(stringsProvider);
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final wait = await ref.read(wordOsApiProvider).resendEmailCode();
      _code.clear();
      _startCooldown(wait);
      if (mounted) {
        ScaffoldMessenger.of(context)
          ..hideCurrentSnackBar()
          ..showSnackBar(SnackBar(content: Text(s.codeSentAgain)));
      }
    } on ApiException catch (e) {
      if (mounted) setState(() => _error = s.apiError(e.code, e.message));
    } catch (_) {
      if (mounted) setState(() => _error = s.somethingWentWrong);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final s = ref.watch(stringsProvider);
    final email = ref.watch(sessionProvider).user?.email ?? '';

    return AuthScaffold(
      title: s.verifyEmailTitle,
      subtitle: s.verifyEmailIntro(email),
      children: [
        TextField(
          controller: _code,
          keyboardType: TextInputType.number,
          autofillHints: const [AutofillHints.oneTimeCode],
          maxLength: 6,
          autofocus: true,
          // A code is a number: never a right-to-left run, which in an Arabic
          // interface makes "014" read as "410".
          textDirection: TextDirection.ltr,
          inputFormatters: [FilteringTextInputFormatter.digitsOnly],
          decoration: InputDecoration(
            labelText: s.resetCode,
            prefixIcon: const Icon(Icons.pin_outlined),
            counterText: '',
          ),
          onSubmitted: (_) => _busy ? null : _verify(),
        ),
        const SizedBox(height: AppSpacing.xs),
        Text(
          s.verifyEmailNotThere,
          style: context.text.bodySmall?.copyWith(
            color: context.colors.onSurface.withValues(alpha: 0.65),
          ),
        ),
        if (_error != null) ...[
          const SizedBox(height: AppSpacing.md),
          AuthErrorBanner(message: _error!),
        ],
        const SizedBox(height: AppSpacing.lg),
        FilledButton(
          onPressed: _busy ? null : _verify,
          child: _busy
              ? const SizedBox(
                  width: 20,
                  height: 20,
                  child: CircularProgressIndicator(
                    strokeWidth: 2.4,
                    color: Colors.white,
                  ),
                )
              : Text(s.verifyEmailButton),
        ),
        const SizedBox(height: AppSpacing.sm),
        TextButton(
          onPressed: _busy || _cooldown > 0 ? null : _resend,
          child: Text(_cooldown > 0 ? s.resendCodeIn(_cooldown) : s.resendCode),
        ),
        TextButton(
          onPressed: _busy
              ? null
              : () => ref.read(sessionProvider.notifier).signOut(),
          child: Text(s.wrongEmailSignOut),
        ),
      ],
    );
  }
}
