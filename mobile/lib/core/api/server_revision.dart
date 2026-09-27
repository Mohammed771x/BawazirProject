import 'package:flutter_riverpod/flutter_riverpod.dart';

/// A counter that changes whenever server state may have moved.
///
/// Every screen that reads from the server watches this, and every call that
/// writes to the server bumps it. That is the whole mechanism (ADR-094).
///
/// **Why it exists.** Each screen used to invalidate the providers it happened
/// to know about: adding a word refreshed the hub and not the word list, so the
/// learner added a word, opened My Words, and it was not there. Finishing a
/// session refreshed the hub from one path and not another. Fourteen call
/// sites, each a thing a future change has to remember — and the failure is
/// silent, because a stale screen looks exactly like a correct one.
///
/// The rule is now the other way round: a provider that reads server state
/// watches this, and nobody has to remember anything. Adding a screen costs one
/// line; adding an endpoint costs nothing, because the bump happens at the
/// bottom of the stack where every write already passes.
///
/// It is deliberately **one** counter rather than one per resource. A finer
/// signal would mean deciding, at each write, which screens it could possibly
/// affect — which is the same guesswork this replaces, and wrong in the same
/// direction: answering a skill changes the hub, the word, the word list, the
/// vocabulary counts and the weekly challenge's backlog. Refetching a screen
/// nobody is looking at costs nothing, because `autoDispose` means nobody is
/// listening to it.
class ServerRevision extends Notifier<int> {
  @override
  int build() => 0;

  /// Something changed. Everything that reads server state re-reads it.
  void bump() => state = state + 1;
}

final serverRevisionProvider =
    NotifierProvider<ServerRevision, int>(ServerRevision.new);

/// Marks a provider as reading server state.
///
/// Call it first in the provider body. Named for what it means rather than for
/// what it does, so a provider that forgets it reads as obviously incomplete:
///
/// ```dart
/// final hubProvider = FutureProvider.autoDispose<HubState>((ref) {
///   refetchWhenServerChanges(ref);
///   return ref.watch(wordOsApiProvider).hub();
/// });
/// ```
///
/// A function rather than an extension on `Ref`: Riverpod 2 hands each provider
/// a differently-typed ref, and an extension does not resolve across all of
/// them.
void refetchWhenServerChanges(Ref<Object?> ref) =>
    ref.watch(serverRevisionProvider);
