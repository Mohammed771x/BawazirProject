import 'package:flutter/material.dart';

import '../theme/app_tokens.dart';

/// The WordOS wordmark and W logo.
///
/// **Always left, in every language.** The brand and the reading direction are
/// two separate concerns: Arabic flips the *content* of the app to RTL, but a
/// logo is a fixed identity, not a paragraph. Left it stays.
///
/// That takes two deliberate steps, because Flutter would otherwise flip both:
///
/// * [Directionality] pinned to `ltr`, so the mark sits before the wordmark
///   rather than after it;
/// * [Alignment.centerLeft] rather than `AlignmentDirectional.centerStart`, so
///   "left" means left and not "start", which is the right-hand edge in Arabic.
///
/// Use this widget wherever the brand appears. A hand-rolled `Row` picks up the
/// ambient directionality and quietly moves the logo the moment the learner
/// switches to Arabic.
class WordOsBrand extends StatelessWidget {
  const WordOsBrand({super.key, this.size = WordOsBrandSize.large});

  final WordOsBrandSize size;

  @override
  Widget build(BuildContext context) {
    final double mark = switch (size) {
      WordOsBrandSize.large => 46,
      WordOsBrandSize.compact => 30,
    };

    return Align(
      // Not AlignmentDirectional: this must be the left edge of the screen even
      // when the surrounding layout runs right-to-left.
      alignment: Alignment.centerLeft,
      child: Directionality(
        textDirection: TextDirection.ltr,
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Container(
              width: mark,
              height: mark,
              decoration: BoxDecoration(
                gradient: LinearGradient(
                  colors: [
                    context.colors.primary,
                    context.palette.skillListening,
                  ],
                  begin: Alignment.topLeft,
                  end: Alignment.bottomRight,
                ),
                borderRadius: BorderRadius.all(
                  size == WordOsBrandSize.large ? AppRadii.md : AppRadii.sm,
                ),
              ),
              alignment: Alignment.center,
              child: Text(
                'W',
                style: TextStyle(
                  color: Colors.white,
                  fontSize: mark * 0.52,
                  fontWeight: FontWeight.w800,
                ),
              ),
            ),
            SizedBox(width: size == WordOsBrandSize.large
                ? AppSpacing.sm
                : AppSpacing.xs),
            Text(
              'WordOS',
              style: size == WordOsBrandSize.large
                  ? context.text.titleLarge
                  : context.text.titleMedium,
            ),
          ],
        ),
      ),
    );
  }
}

enum WordOsBrandSize {
  /// Onboarding and authentication, where the brand is the focal point.
  large,

  /// App bars and dense headers.
  compact,
}
