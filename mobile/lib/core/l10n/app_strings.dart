import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../models/enums.dart';
import '../storage/app_preferences.dart';
import '../storage/preferences_providers.dart';

/// Lightweight bilingual strings (ADR-004).
///
/// The interface is English + Arabic with full RTL; the *learning content*
/// stays English and the word meanings stay Arabic, as the documents specify.
class AppStrings {
  const AppStrings(this.locale);

  final Locale locale;

  bool get isArabic => locale.languageCode == 'ar';

  String _(String en, String ar) => isArabic ? ar : en;

  // Brand & generic
  String get appName => 'WordOS';
  String get tagline =>
      _('Turn every word into language you can use.', 'حوّل كل كلمة إلى لغة تستطيع استخدامها.');
  String get continueLabel => _('Continue', 'متابعة');
  String get next => _('Next', 'التالي');
  String get done => _('Done', 'تم');
  String get cancel => _('Cancel', 'إلغاء');
  String get save => _('Save', 'حفظ');
  String get retry => _('Try again', 'إعادة المحاولة');
  String get close => _('Close', 'إغلاق');
  String get copy => _('Copy', 'نسخ');
  String get finish => _('Finish', 'إنهاء');
  String get start => _('Start', 'ابدأ');
  String get skip => _('Skip', 'تخطي');
  /// What went wrong, in the learner's language.
  ///
  /// The API answers with a stable `code` and an English sentence. The code is
  /// the contract; the sentence is a fallback for anything this app has not met
  /// yet — and it is only ever shown when there is nothing better, because an
  /// English error in an Arabic app reads as a crash (ADR-035).
  String apiError(String code, String fallback) => switch (code) {
        // Getting in.
        'INVALID_CREDENTIALS' => _(
            'Wrong email or password.',
            'البريد الإلكتروني أو كلمة المرور غير صحيحة.',
          ),
        // The server's own rule, said in the learner's language (ADR-054).
        'INVALID_PHONE' => _(
            'Enter a valid phone number.',
            'أدخل رقم هاتف صحيحًا.',
          ),
        'EMAIL_TAKEN' => _(
            'That email already has an account.',
            'يوجد حساب بهذا البريد الإلكتروني بالفعل.',
          ),

        // Adding words.
        'WORD_ALREADY_ADDED' => alreadyInYourWords,
        'WORD_NOT_FOUND' => _(
            'That word and meaning are not in the dictionary.',
            'هذه الكلمة بهذا المعنى غير موجودة في القاموس.',
          ),

        // Sessions.
        'NO_WORDS_DUE' => _(
            'Nothing is due for this skill yet.',
            'لا توجد كلمات مستحقة في هذه المهارة الآن.',
          ),
        'EMPTY_ANSWER' => _('Write a sentence first.', 'اكتب جملة أولًا.'),
        'EMPTY_TURN' => _('Say something first.', 'قل شيئًا أولًا.'),
        'SESSION_COMPLETE' => _(
            'This session is already finished.',
            'انتهت هذه الجلسة بالفعل.',
          ),
        'SESSION_NOT_FOUND' => _(
            'This session is no longer available.',
            'لم تعد هذه الجلسة متاحة.',
          ),
        'ITEM_NOT_CURRENT' => _(
            'That question is no longer the active one.',
            'لم يعد هذا السؤال هو السؤال الحالي.',
          ),
        'SESSION_STARTED' => _(
            'The level cannot change once the questions have begun.',
            'لا يمكن تغيير المستوى بعد بدء الأسئلة.',
          ),
        'RELEVEL_UNAVAILABLE' => _(
            'The passage could not be rewritten just now. Try again.',
            'تعذّرت إعادة صياغة النص الآن. حاول مرة أخرى.',
          ),

        // Interests and review.
        'NO_INTERESTS' => _(
            'Choose at least one interest.',
            'اختر اهتمامًا واحدًا على الأقل.',
          ),
        'TOO_MANY_INTERESTS' => _(
            'That is more interests than we can use.',
            'عدد الاهتمامات أكبر مما يمكن استخدامه.',
          ),
        'NO_WORDS_IN_PERIOD' => _(
            'No words were studied in this period.',
            'لا توجد كلمات دُرست في هذه الفترة.',
          ),
        'REVIEW_COMPLETE' => _(
            'This review is already finished.',
            'انتهت هذه المراجعة بالفعل.',
          ),
        'REVIEW_NOT_FOUND' => _(
            'This review is no longer available.',
            'لم تعد هذه المراجعة متاحة.',
          ),

        // Placement.
        'PLACEMENT_NOT_FOUND' => _(
            'This placement test is no longer available. Start it again.',
            'لم يعد اختبار تحديد المستوى هذا متاحًا. ابدأه من جديد.',
          ),
        'PLACEMENT_COMPLETE' => _(
            'You have already finished the placement test.',
            'لقد أنهيت اختبار تحديد المستوى بالفعل.',
          ),
        'PLACEMENT_INCOMPLETE' => _(
            'There are still questions left to answer.',
            'ما زالت هناك أسئلة لم تُجب عنها.',
          ),

        // ── The learner is not to blame, and waiting fixes it ──────────────
        //
        // These three are the whole reason 503 exists in this service: too many
        // people at once, not a fault. The wording says so, because "something
        // went wrong" invites the learner to think their answer was lost — and
        // nothing of theirs was even touched (ADR-051).
        'SERVER_BUSY' => _(
            'The server is busy right now. Try again in a moment.',
            'الخادم مشغول الآن. حاول بعد لحظات.',
          ),
        'AI_BUSY' => _(
            'Lessons are in high demand right now. Try again in a moment.',
            'الطلب على الدروس مرتفع الآن. حاول بعد لحظات.',
          ),
        'SESSION_STARTING' || 'SESSION_RACE' => _(
            'Your session is still being prepared. Try again in a moment.',
            'ما زال يجري تحضير جلستك. حاول بعد لحظات.',
          ),

        // ── The session ended somewhere else ───────────────────────────────
        //
        // Signing in again is the only way out of these, so they say that
        // rather than describing the mechanism. `REFRESH_REUSED` means a token
        // was presented twice and every session was revoked — the learner did
        // nothing wrong and does not need to be alarmed, they just need to
        // sign in.
        'INVALID_REFRESH' || 'REFRESH_REUSED' || 'UNAUTHORIZED' => _(
            'Your session has ended. Please sign in again.',
            'انتهت جلستك. يُرجى تسجيل الدخول مرة أخرى.',
          ),
        'FORBIDDEN' => _(
            'You do not have access to this.',
            'ليس لديك صلاحية للوصول إلى هذا.',
          ),

        // ── Connection ─────────────────────────────────────────────────────
        //
        // Separated on purpose. "No connection" is something the learner can
        // act on; "the server took too long" is not their doing and retrying
        // usually works. Collapsing both into one message sends people to
        // restart a router that was never the problem.
        'NETWORK' => _(
            'No connection. Check your internet and try again.',
            'لا يوجد اتصال. تحقّق من الإنترنت وحاول مرة أخرى.',
          ),
        'TIMEOUT' => _(
            'The connection is slow and the request timed out. Try again.',
            'الاتصال بطيء وانتهت مهلة الطلب. حاول مرة أخرى.',
          ),
        'INSECURE_CONNECTION' => _(
            'This connection could not be trusted, so it was stopped.',
            'تعذّر الوثوق بهذا الاتصال، لذا تم إيقافه.',
          ),
        'RATE_LIMITED' => _(
            'Too many requests. Wait a moment and try again.',
            'طلبات كثيرة جدًا. انتظر لحظة ثم حاول مرة أخرى.',
          ),

        // ── Nothing the learner can do anything about ──────────────────────
        //
        // Deliberately vague about the cause and specific about what happens
        // next. The detail lives in the server's log, where an attacker cannot
        // read it (docs/07-SECURITY.md §8) — and where it is of no use to a
        // learner anyway.
        'INTERNAL_ERROR' || 'SERVER_ERROR' => _(
            'Something went wrong on our side. Please try again.',
            'حدث خطأ لدينا. يُرجى المحاولة مرة أخرى.',
          ),
        // The response was not what this version of the app expects — an older
        // app against a newer API, or a captive-portal page instead of JSON.
        'BAD_RESPONSE' => _(
            'The app could not read the reply. Try updating the app.',
            'تعذّر على التطبيق قراءة الرد. جرّب تحديث التطبيق.',
          ),
        // Raised by the app about itself, never by the server.
        'UNEXPECTED' => somethingWentWrong,

        // ── Requests that should not have been sent ────────────────────────
        //
        // A learner cannot reach these by tapping: they mean this app sent
        // something the API refused. Shown plainly rather than as a fault the
        // learner must solve, because there is nothing for them to solve.
        'INVALID_PARAMETER' || 'INVALID_SKILL' || 'INVALID_LEVEL' ||
        'INVALID_STATE' || 'INVALID_STATUS' || 'WRONG_SKILL' ||
        'WRONG_ITEM_TYPE' || 'VALIDATION_FAILED' =>
          _(
            'That request could not be completed. Please try again.',
            'تعذّر إتمام هذا الطلب. يُرجى المحاولة مرة أخرى.',
          ),
        'ITEM_NOT_FOUND' => _(
            'That question is no longer available.',
            'لم يعد هذا السؤال متاحًا.',
          ),
        // A 409 that arrived with no body of its own, so there is no more
        // specific code to say what changed underneath.
        'CONFLICT' => _(
            'That is not available right now.',
            'هذا غير متاح في الوقت الحالي.',
          ),
        'UNKNOWN' => somethingWentWrong,
        'NOT_FOUND' || 'USER_NOT_FOUND' => _(
            'That is no longer available.',
            'لم يعد هذا متاحًا.',
          ),
        'NO_CONTENT' => _(
            'This session has no content to show.',
            'لا يوجد محتوى لعرضه في هذه الجلسة.',
          ),
        'NO_WARMUP' => _(
            'There is no warm-up for this session.',
            'لا توجد تهيئة لهذه الجلسة.',
          ),
        'LEVEL_NOT_ADJUSTABLE' || 'SKILL_NOT_LEVELLED' => _(
            'This skill does not have a level to change.',
            'هذه المهارة ليس لها مستوى يمكن تغييره.',
          ),

        // ── Searching the dictionary ───────────────────────────────────────
        'QUERY_TOO_LONG' => _(
            'That search term is too long.',
            'كلمة البحث طويلة جدًا.',
          ),
        'BAD_WORD' => _(
            'Enter a single word to look up.',
            'أدخل كلمة واحدة للبحث عنها.',
          ),
        'EMPTY_FEEDBACK' => _('Write a message first.', 'اكتب رسالة أولًا.'),

        // A cancelled request is the app tidying up after a screen the learner
        // already left. There is nobody to tell, and saying anything would be
        // an error message for something they did on purpose.
        'CANCELLED' => '',

        // Anything this app has not met. The server writes its sentences for a
        // learner to read, so the fallback is safe to show — but a blank one
        // would be a dialog with no text in it, so it never gets that far.
        _ => fallback.trim().isEmpty ? somethingWentWrong : fallback,
      };

  String get somethingWentWrong =>
      _('Something went wrong', 'حدث خطأ ما');
  String get loading => _('Loading…', 'جارٍ التحميل…');

  // ── Onboarding ────────────────────────────────────────────────────────────
  //
  // Short by design. A first-time learner decides whether to continue in a few
  // seconds, and a paragraph is the fastest way to lose them.
  String get onboardingWelcomeTitle =>
      _('Welcome to WordOS', 'أهلًا بك في WordOS');
  String get onboardingWelcomeBody => _(
        'Your own vocabulary, turned into real English you can actually use.',
        'مفرداتك الخاصة، تتحوّل إلى إنجليزية حقيقية تستطيع استخدامها فعلًا.',
      );

  String get onboardingWordsTitle =>
      _('Add the words you meet', 'أضف الكلمات التي تصادفها');
  String get onboardingWordsBody => _(
        'Not another memorisation app. WordOS keeps every word you add and '
            'brings it back until it sticks.',
        'ليس تطبيق حفظ آخر. يحتفظ WordOS بكل كلمة تضيفها ويعيدها إليك حتى '
            'ترسخ في ذاكرتك.',
      );

  String get onboardingSkillsTitle =>
      _('Practise the language, not flashcards', 'تدرّب على اللغة، لا البطاقات');
  String get onboardingSkillsBody => _(
        'Every word travels through reading, listening, speaking and writing — '
            'so you stop switching between apps.',
        'كل كلمة تمرّ عبر القراءة والاستماع والتحدث والكتابة — لتتوقف عن '
            'التنقل بين التطبيقات.',
      );

  String get onboardingStartTitle =>
      _('Ready when you are', 'جاهز عندما تكون مستعدًا');
  String get onboardingStartBody => _(
        'Create your account, take a short placement test, and start at the '
            'right level.',
        'أنشئ حسابك، واخضع لاختبار تحديد مستوى قصير، وابدأ من المستوى المناسب لك.',
      );

  String get onboardingCreateAccount =>
      _('Create your account', 'أنشئ حسابك');
  String get onboardingHaveAccount =>
      _('I already have an account', 'لديّ حساب بالفعل');

  // Auth
  String get phoneNumber => _('Phone number', 'رقم الهاتف');
  String get phoneRequired => _(
        'Enter your phone number so we can reach you.',
        'أدخل رقم هاتفك حتى نتمكن من التواصل معك.',
      );
  String get signIn => _('Sign in', 'تسجيل الدخول');
  String get signUp => _('Create account', 'إنشاء حساب');
  String get signOut => _('Sign out', 'تسجيل الخروج');
  String get signOutConfirm => _(
        'You will need to sign in again to continue learning.',
        'ستحتاج إلى تسجيل الدخول مرة أخرى لمتابعة التعلّم.',
      );
  String get email => _('Email', 'البريد الإلكتروني');
  String get password => _('Password', 'كلمة المرور');
  String get name => _('Your name', 'اسمك');
  String get welcomeBack => _('Welcome back', 'أهلًا بعودتك');
  String get createYourAccount => _('Create your account', 'أنشئ حسابك');
  String get noAccountYet =>
      _("Don't have an account?", 'ليس لديك حساب؟');
  String get alreadyHaveAccount =>
      _('Already have an account?', 'لديك حساب بالفعل؟');
  String get emailRequired => _('Enter your email', 'أدخل بريدك الإلكتروني');
  /// The **server's** rule, said before the request is sent.
  ///
  /// This used to say six while `RegisterRequest` required eight, so a
  /// seven-character password passed the form, was refused by the API, and came
  /// back as a validation failure that named no field — the learner was told
  /// their details were wrong without being told which, or how.
  ///
  /// Keep this number and [minPasswordLength] in step with `MinLength(8)` in
  /// `AuthEndpoints.RegisterRequest`.
  String get passwordRequired => _(
        'Password must be at least $minPasswordLength characters',
        'كلمة المرور $minPasswordLength أحرف على الأقل',
      );

  /// What the API will accept, so the form can refuse it first.
  static const int minPasswordLength = 8;
  String get demoHint => _(
        'Demo account: demo@wordos.app / wordos123',
        'حساب تجريبي: demo@wordos.app / wordos123',
      );

  // Onboarding
  String get interestsTitle => _('What interests you?', 'ما الذي يهمك؟');
  String get interestsSubtitle => _(
        'We use your interests to make the content feel relevant — never to force a topic.',
        'نستخدم اهتماماتك لجعل المحتوى قريبًا منك، دون فرض الموضوع على المحتوى.',
      );
  String get otherInterest => _('Other', 'أخرى');
  String get addInterest => _('Add an interest', 'أضف اهتمامًا');
  String get customInterestHint => _(
        'e.g. astronomy, cooking, chess',
        'مثال: الفلك، الطبخ، الشطرنج',
      );
  String get interestsPickMany => _(
        'Pick everything that applies — the more you choose, the more personal your content becomes.',
        'اختر كل ما ينطبق عليك — كلما اخترت أكثر، أصبح المحتوى أقرب إليك.',
      );
  String interestsSelectedCount(int count) => _(
        '$count selected — keep going if more apply.',
        'اخترت $count — أضف المزيد إن وُجد.',
      );
  String get interestsKeepAtLeastOne => _(
        'Keep at least one interest so we can shape your content.',
        'أبقِ اهتمامًا واحدًا على الأقل حتى نتمكن من تخصيص المحتوى.',
      );
  String get placementTitle => _('Placement test', 'اختبار تحديد المستوى');
  String get placementSubtitle => _(
        'A short check to find your starting point for each skill. It is not a certificate.',
        'اختبار قصير لتحديد نقطة البداية لكل مهارة، وليس شهادة نهائية.',
      );
  String get placementAdaptiveNote => _(
        'The questions adapt to your answers, so the test stays short and stops '
            'as soon as it is sure.',
        'تتكيّف الأسئلة مع إجاباتك، لذا يبقى الاختبار قصيرًا وينتهي فور التأكد من مستواك.',
      );
  String placementApproxProgress(int current, int approxTotal) => _(
        '$current of ~$approxTotal',
        '$current من ~$approxTotal',
      );
  String get placementProvisional => _(
        'Provisional — we will confirm this from your first sessions',
        'مبدئي — سنؤكده من جلساتك الأولى',
      );
  // An estimate, said as an estimate. A placement result is the first thing
  // WordOS ever tells a learner about themselves, and "you are a beginner"
  // reads as a verdict on the person. The levels are the app's current guess,
  // they move with real performance, and the learner can change them — so all
  // three are said plainly.
  String get placementResultTitle =>
      _('Your estimated levels', 'مستوياتك التقديرية');
  String get placementEstimateNote => _(
        'These are estimates from a short test — not a judgement of you. '
        'WordOS keeps adjusting them from your real sessions, and you can '
        'change any of them yourself in Settings.',
        'هذه تقديرات من اختبار قصير — وليست حكمًا عليك. يواصل WordOS تعديلها '
        'من جلساتك الفعلية، ويمكنك تغيير أيٍّ منها بنفسك من الإعدادات.',
      );
  String get spellingMeasured => _('Measured', 'مُقاسة');
  String spellingAccuracyLabel(int correct, int total) => _(
        '$correct of $total correct',
        '$correct من $total صحيحة',
      );
  String get startLearning => _('Start learning', 'ابدأ التعلم');
  String get writeYourAnswer => _('Write your answer', 'اكتب إجابتك');
  String get playAudio => _('Play audio', 'تشغيل الصوت');

  // Hub
  String get skillsHub => _('Skills Hub', 'مركز المهارات');
  String get todayProgress => _('Words added today', 'كلمات أضيفت اليوم');
  String get weeklyReview => _('Weekly Review', 'المراجعة الأسبوعية');
  String get weeklyReviewSubtitle => _(
        'Review this week\'s words. It measures memory only — it never changes your pipeline.',
        'مراجعة كلمات الأسبوع. للقياس فقط ولا تغيّر مسار الكلمات.',
      );
  String wordsDue(int count) => _(
        count == 1 ? '1 word ready' : '$count words ready',
        count == 1 ? 'كلمة واحدة جاهزة' : '$count كلمات جاهزة',
      );
  String get nothingDue => _('Nothing due yet', 'لا يوجد مستحق الآن');
  String nextDue(String when) =>
      _('Next on $when', 'التالي في $when');
  String get openSkill => _('Start session', 'ابدأ الجلسة');

  // Words
  String get addWord => _('Add word', 'إضافة كلمة');
  String get vocabulary => _('Vocabulary', 'المفردات');
  // The field takes either language: an Arabic word returns the English words
  // that mean it, so a learner who knows what they want to say can still find
  // it (ADR-034).
  String get typeWord => _(
        'Type a word — in English or Arabic',
        'اكتب كلمة — بالإنجليزية أو بالعربية',
      );
  String get chooseWord => _('Choose a word', 'اختر كلمة');
  String get chooseMeaning => _(
        'Which meaning do you mean?',
        'ما المعنى الذي تقصده؟',
      );
  String get chooseMeaningSubtitle => _(
        'A word with two meanings is two different learning journeys.',
        'الكلمة بمعنيين تعني رحلتي تعلّم مختلفتين.',
      );
  String wordNotFound(String query) => _(
        '"$query" is not in the dictionary',
        '«$query» غير موجودة في القاموس',
      );
  String get wordNotFoundBody => _(
        'WordOS only adds words it can verify, with a meaning from a trusted source — that is what keeps your vocabulary reliable.',
        'يضيف WordOS الكلمات التي يستطيع التحقق منها فقط، بمعنى من مصدر موثوق، وهذا ما يحافظ على دقة مفرداتك.',
      );
  String get wordAdded => _('Added to the learning pipeline', 'أُضيفت إلى مسار التعلّم');
  String get analyzingWord =>
      _('Analyzing the word…', 'جارٍ تحليل الكلمة…');
  String get learning => _('Learning', 'قيد التعلّم');
  String get active => _('Active', 'نشطة');
  String get archived => _('Archived', 'مؤرشفة');
  String get noWordsYet => _('No words here yet', 'لا توجد كلمات هنا بعد');
  // My Words (Part 2 §42–§46). One list, searchable — the pipeline states are
  // how the system thinks about a word, not how a learner looks one up.
  String get myWords => _('My words', 'كلماتي');
  String get searchYourWords =>
      _('Search your words', 'ابحث في كلماتك');
  String get noWordsMatch =>
      _('No words match that search', 'لا توجد كلمات تطابق البحث');
  String wordCount(int count) => _(
        count == 1 ? '1 word' : '$count words',
        count == 1 ? 'كلمة واحدة' : '$count كلمة',
      );
  String get wordKnown => _('Known', 'مُتقنة');
  String get wordJourney => _('Word journey', 'رحلة الكلمة');
  String get exposure => _('Exposure', 'مرات الاستخدام');
  String get addedOn => _('Added', 'أُضيفت');
  String get spellingSuggestion => _('Did you mean', 'هل تقصد');

  // Skills
  String skillName(SkillType skill) => switch (skill) {
        SkillType.reading => _('Reading', 'القراءة'),
        SkillType.listening => _('Listening', 'الاستماع'),
        SkillType.speaking => _('Speaking', 'التحدث'),
        SkillType.writing => _('Writing', 'الكتابة'),
        SkillType.spelling => _('Spelling', 'التهجئة'),
      };

  String skillTagline(SkillType skill) => switch (skill) {
        SkillType.reading =>
          _('Understand the word in written context', 'افهم الكلمة داخل نص مكتوب'),
        SkillType.listening =>
          _('Understand the word when you hear it', 'افهم الكلمة عند سماعها'),
        SkillType.speaking =>
          _('Use the word while speaking', 'استخدم الكلمة أثناء التحدث'),
        SkillType.writing =>
          _('Produce the word in writing', 'استخدم الكلمة في الكتابة'),
        SkillType.spelling =>
          _('Write the correct form', 'اكتب الشكل الصحيح للكلمة'),
      };

  String statusLabel(SkillStatus status) => switch (status) {
        SkillStatus.pending => _('Waiting', 'بالانتظار'),
        SkillStatus.available => _('Ready', 'جاهزة'),
        SkillStatus.passed => _('Passed', 'ناجحة'),
        SkillStatus.failed => _('Retry', 'إعادة'),
      };

  /// The Owner's word journey reads these; a learner never sees them, because
  /// they name pipeline machinery rather than learning (Part 3).
  String wordEventLabel(WordEventType type) => switch (type) {
        WordEventType.added => _('Added', 'أُضيفت'),
        WordEventType.skillStarted => _('Session started', 'بدأت الجلسة'),
        WordEventType.skillPassed => _('Passed', 'نجحت'),
        WordEventType.skillFailed => _('Failed', 'أخفقت'),
        WordEventType.becameMature => _('Matured', 'نضجت'),
        WordEventType.enteredActive =>
          _('Entered active vocabulary', 'دخلت المفردات النشطة'),
        WordEventType.exposureIncremented => _('Reused', 'أُعيد استخدامها'),
        WordEventType.archived => _('Archived', 'أُرشفت'),
      };

  String get pipeline => _('Pipeline', 'المسار');
  String devSkillsPassed(int count) =>
      _('$count/5 skills', '$count/5 مهارات');
  String devAttempts(int count) => _('$count attempts', '$count محاولة');
  String get devJourneyHint => _(
        'Every recorded event for this word, oldest first.',
        'كل حدث مسجّل لهذه الكلمة، من الأقدم إلى الأحدث.',
      );
  String get devNoEvents => _('Nothing recorded yet', 'لا توجد أحداث بعد');
  String get devExposureHint => _(
        'When this word was reused in generated content or a review.',
        'متى أُعيد استخدام هذه الكلمة في محتوى مُولَّد أو مراجعة.',
      );

  String stateLabel(WordState state) => switch (state) {
        WordState.learning => learning,
        WordState.mature => _('Mature', 'ناضجة'),
        WordState.active => active,
        WordState.archived => archived,
      };

  // Sessions
  String get readPassage => _('Read the passage', 'اقرأ النص');
  String get listenCarefully => _('Listen carefully', 'استمع بتركيز');
  String get playAgain => _('Play again', 'إعادة التشغيل');
  String get slowSpeed => _('Slow', 'بطيء');
  String get normalSpeed => _('Normal', 'عادي');
  String get showTranscript => _('Show transcript', 'إظهار النص');
  String get iFinishedReading => _('I finished reading', 'أنهيت القراءة');
  String get iFinishedListening => _('I finished listening', 'أنهيت الاستماع');
  String questionOf(int current, int total) =>
      _('Question $current of $total', 'السؤال $current من $total');
  String retryAttempt(int attempt) =>
      _('Attempt $attempt', 'المحاولة $attempt');
  String get comesBackLater => _(
        'This one will come back before the session ends',
        'ستعود هذه قبل نهاية الجلسة',
      );
  String get guessFromContext => _(
        'Use the sentences around it to work out the meaning.',
        'استعن بالجمل المحيطة لاستنتاج المعنى.',
      );
  // Tapping a word inside a passage (Part 2 §17–§19).
  /// What kind of word this is, in the interface language.
  ///
  /// A learner needs it to understand what they are adding: "will" as an
  /// auxiliary is a different thing to learn than "will" as a noun. The
  /// generator answers for the sentence at hand, so the same word can be a
  /// verb in one passage and a noun in another.
  /// Every code the lexicon actually stores, in both languages.
  ///
  /// The closed-class words are imported with short codes — `prep`, `det`,
  /// `aux`, `modal` — and only the spelled-out names were listed here, so the
  /// fallthrough printed the raw code at the learner: a word tagged `modal`
  /// read as "modal" in an Arabic interface (ADR-056).
  String partOfSpeechLabel(String raw) => switch (raw.trim().toLowerCase()) {
        'noun' || 'n' => _('noun', 'اسم'),
        'verb' || 'v' => _('verb', 'فعل'),
        // `s` is WordNet's adjective satellite — an adjective to a learner.
        'adjective' || 'adj' || 'a' || 's' => _('adjective', 'صفة'),
        'adverb' || 'adv' || 'r' => _('adverb', 'ظرف'),
        'pronoun' || 'pron' => _('pronoun', 'ضمير'),
        'preposition' || 'prep' => _('preposition', 'حرف جر'),
        'conjunction' || 'conj' => _('conjunction', 'أداة ربط'),
        'determiner' || 'det' => _('determiner', 'أداة تعريف'),
        'auxiliary' || 'aux' => _('auxiliary verb', 'فعل مساعد'),
        'modal' => _('modal verb', 'فعل ناقص'),
        'interjection' || 'intj' => _('interjection', 'أداة تعجّب'),
        'particle' || 'part' => _('particle', 'أداة'),
        'numeral' || 'num' => _('number', 'عدد'),
        // Better an empty chip than a raw code shown to a learner.
        _ => raw,
      };

  /// Which form of the word this entry is (ADR-056).
  ///
  /// Null for a word in its plain form: a learner looking at `book` needs no
  /// label telling them it is `book`.
  String? wordFormLabel(String? key) => switch (key) {
        'past' => _('past tense', 'الماضي'),
        'pastParticiple' => _('past participle', 'اسم المفعول'),
        'ing' => _('-ing form', 'صيغة ing'),
        'plural' => _('plural', 'جمع'),
        _ => null,
      };

  // Changing the level of a passage (§4). Said as "the same text, easier" —
  // because that is what happens, and a learner who fears losing the story
  // will not press it otherwise.
  String get changeLevelTitle =>
      _('Change the level', 'غيّر المستوى');
  String get changeLevelHint => _(
        'The same passage, re-told at the level you choose. Only before you '
        'start the questions.',
        'النص نفسه، مُعاد صياغته بالمستوى الذي تختاره. متاح قبل بدء الأسئلة فقط.',
      );
  String get changeLevelHintSpeaking => _(
        'The conversation continues at the level you choose, from the tutor\'s '
        'next reply onwards.',
        'تستمر المحادثة بالمستوى الذي تختاره، ابتداءً من رد المدرّس التالي.',
      );
  String get meaningHere => _('Meaning here', 'المعنى هنا');
  String get tapAnyWord => _(
        'Tap any word to hear it and see what it means.',
        'اضغط على أي كلمة لسماعها ومعرفة معناها.',
      );
  String get targetWordNoMeaning => _(
        'This is one of the words in this session — its meaning stays hidden '
        'until the questions are done.',
        'هذه إحدى كلمات هذه الجلسة — يبقى معناها مخفيًا حتى تنتهي الأسئلة.',
      );
  String get noDictionaryEntry => _(
        'No dictionary entry for this word. You can still hear it.',
        'لا يوجد مدخل في القاموس لهذه الكلمة. لا يزال بإمكانك سماعها.',
      );
  String fromWord(String base) => _('from "$base"', 'من «$base»');
  String get addToMyWords => _('Add to my words', 'أضفها إلى كلماتي');
  String get alreadyInYourWords => _('Already in your words', 'موجودة في كلماتك');
  // Speaking: the pre-conversation briefing (Part 2 §26).
  String get beforeYouSpeak => _('Before you start', 'قبل أن تبدأ');
  String get warmupHint => _(
        'A quick check before you talk. It is not scored — a word you miss '
        'simply comes back.',
        'مراجعة سريعة قبل الحديث. لا تُحتسب في تقييمك — والكلمة التي تخطئ '
        'فيها تعود إليك ببساطة.',
      );
  String warmupRemaining(int count) => _(
        count == 1 ? '1 word left' : '$count words left',
        count == 1 ? 'بقيت كلمة واحدة' : 'بقيت $count كلمات',
      );
  String get beforeYouSpeakHint => _(
        'You are about to have a short conversation. Try to use these words '
        'naturally — listen to each one first if you need to.',
        'ستجري محادثة قصيرة. حاول استخدام هذه الكلمات بشكل طبيعي — '
        'استمع إلى كل كلمة أولًا إن احتجت.',
      );
  String get startConversation => _('Start the conversation', 'ابدأ المحادثة');
  String get listenToSentence => _('Listen to this sentence', 'استمع إلى هذه الجملة');
  String get audioUnavailable => _(
        'Audio is unavailable on this device — here is the sentence instead',
        'الصوت غير متاح على هذا الجهاز — إليك الجملة بدلًا من ذلك',
      );
  String get undoLetter => _('Undo last letter', 'تراجع عن آخر حرف');
  String get showHint => _('Need a hint?', 'تحتاج تلميحًا؟');

  /// Every press steps one rung down the ladder, so the label has to promise
  /// something *easier* than the help already on screen — otherwise it reads
  /// as the same button repeated.
  String get easierHint => _('Something easier', 'مساعدة أسهل');
  String spellingClueLabel(SpellingClueKind? kind) => switch (kind) {
        SpellingClueKind.definitionEn => _('Definition', 'التعريف'),
        SpellingClueKind.simplifiedDefinition =>
          _('In simpler words', 'بكلمات أبسط'),
        SpellingClueKind.synonym => _('Similar word', 'كلمة مرادفة'),
        SpellingClueKind.letterCount =>
          _('Number of letters', 'عدد الحروف'),
        _ => _('Meaning', 'المعنى'),
      };
  String get correct => _('Correct', 'إجابة صحيحة');
  String get incorrect => _('Not quite', 'إجابة غير صحيحة');
  String get correctAnswerIs => _('Correct answer', 'الإجابة الصحيحة');
  /// The instruction for one session item.
  ///
  /// Said in the learner's language, because it is the app talking rather than
  /// the material being taught. Anything the server wrote for this session in
  /// particular — a comprehension question, its options — has no key and is
  /// shown exactly as it arrived (ADR-035).
  String sessionPrompt(SessionPromptKey? key, String word) => switch (key) {
        SessionPromptKey.writeTheWord => _('Write the word', 'اكتب الكلمة'),
        SessionPromptKey.writeASentence => _(
            'Write one sentence using "$word".',
            'اكتب جملة واحدة تستخدم فيها «$word».',
          ),
        SessionPromptKey.writeASentenceAboutYourself => _(
            'Write one sentence about your own life using "$word".',
            'اكتب جملة واحدة عن حياتك تستخدم فيها «$word».',
          ),
        null => '',
      };

  /// The heading above the rewrite a learner is shown after writing.
  ///
  /// Named for what it is: the same sentence at their level, not a red pen.
  /// A learner who reads it as a correction concludes they made a mistake even
  /// when they did not (ADR-038).
  String atYourLevel(CefrLevel level) => _(
        'Your sentence at ${level.label}',
        'جملتك بمستوى ${level.label}',
      );

  /// Above the model sentence a learner can copy after a conversation.
  String get sayItLikeThis => _('Say it like this', 'قُلها هكذا');

  String get writeSentence =>
      _('Write your sentence', 'اكتب جملتك');
  String get evaluating => _('Evaluating…', 'جارٍ التقييم…');
  String get sendMessage => _('Send', 'إرسال');
  String get yourTurn => _('Your turn — type what you would say',
      'دورك — اكتب ما ستقوله');
  String get speakingHint => _(
        'Speak naturally — the microphone opens by itself when your tutor '
            'finishes.',
        'تحدث بشكل طبيعي — سيفتح الميكروفون تلقائيًا عندما ينتهي معلمك.',
      );

  // ── Voice conversation ────────────────────────────────────────────────────
  String get tutorSpeaking => _('Your tutor is speaking…', 'معلمك يتحدث…');
  String get listeningNow => _('Listening — go ahead', 'أستمع إليك — تفضل');
  String get thinking => _('Thinking…', 'أفكر…');
  String get tapToSpeak => _('Tap to speak', 'اضغط للتحدث');
  String get didNotCatchThat => _(
        "I didn't catch that. Tap the microphone and try again.",
        'لم أسمع ذلك. اضغط على الميكروفون وحاول مرة أخرى.',
      );
  String get microphoneUnavailable => _(
        'This device cannot listen, so you can type your turn instead.',
        'هذا الجهاز لا يستطيع الاستماع، يمكنك كتابة دورك بدلًا من ذلك.',
      );
  // Push-to-talk. The learner starts and ends their own turn — nothing is
  // waiting for them to fall silent, because a learner hunting for a word in a
  // foreign language pauses constantly.
  String get tapWhenDone =>
      _('Speak freely — tap when you finish', 'تحدّث براحتك — اضغط عند الانتهاء');
  String get typeInstead => _('Type instead', 'اكتب بدلًا من ذلك');
  String get useVoice => _('Use voice', 'استخدم الصوت');
  String get discardRecording =>
      _('Delete and record again', 'احذف وسجّل من جديد');
  String get replayTurn => _('Say that again', 'أعد ما قلته');
  // ── Spoken placement ──────────────────────────────────────────────────────
  String get answerOutLoud => _('Answer out loud', 'أجب بصوتك');
  String get tapThenSpeak =>
      _('Tap the microphone and answer', 'اضغط الميكروفون وأجب');
  String get listeningToYou => _('Listening…', 'أستمع إليك…');
  String get yourAnswer => _('Your answer', 'إجابتك');
  String get speakAgain => _('Answer again', 'أجب مرة أخرى');
  String get micUnavailableType => _(
        'This device cannot listen. You can type your answer instead.',
        'هذا الجهاز لا يستطيع الاستماع. يمكنك كتابة إجابتك بدلًا من ذلك.',
      );
  // The recording, handed back after the test (§5). During the session it was
  // the test itself; afterwards it is study material.
  String get listenAgain => _('Listen again', 'استمع مرة أخرى');
  String get stopAudio => _('Stop', 'إيقاف');
  String get tapLetters => _('Tap the letters in order', 'اضغط الحروف بالترتيب');
  String get clear => _('Clear', 'مسح');
  String get checkAnswer => _('Check', 'تحقق');
  String get sessionComplete => _('Session complete', 'انتهت الجلسة');
  String get backToHub => _('Back to Skills Hub', 'العودة إلى مركز المهارات');
  String get comprehension => _('Comprehension', 'فهم المحتوى');
  String becameActiveMsg(String word) => _(
        '"$word" completed all five skills and moved to Active Vocabulary.',
        '«$word» أكملت المهارات الخمس وانتقلت إلى المفردات النشطة.',
      );
  String nextSkillOn(String skill, String when) =>
      _('$skill on $when', '$skill في $when');
  String get retryScheduled =>
      _('Will come back for another try', 'ستعود لمحاولة أخرى');
  String get noWordsDue => _(
        'No words are due for this skill yet.',
        'لا توجد كلمات مستحقة لهذه المهارة الآن.',
      );
  String get noWordsDueBody => _(
        'Words become available after the scheduled gap between skills — that gap is what makes the memory work.',
        'تصبح الكلمات متاحة بعد الفاصل الزمني بين المهارات، وهذا الفاصل هو ما يقوّي التذكّر.',
      );

  // The empty-pipeline fallback (Part 2 §5). Practice is real content and real
  // questions, but it owns no words — so the offer says exactly that, and the
  // session says it again while it runs.
  String get practiceOfferBody => _(
        'Words become available after the scheduled gap between skills. You can '
        'still practise with a passage in the meantime — it will not change any '
        'of your words.',
        'تصبح الكلمات متاحة بعد الفاصل الزمني بين المهارات. يمكنك في هذه '
        'الأثناء التدرّب على نص قرائي — ولن يؤثر ذلك على أي من كلماتك.',
      );
  String get practiseAnyway => _('Practise anyway', 'تدرّب على أي حال');
  String get practiceSession => _('Practice', 'تدريب');
  String get practiceNotCounted => _(
        'Practice only — your words are not affected.',
        'تدريب فقط — لا تتأثر كلماتك.',
      );

  // Weekly review
  String get reviewRemaining => _('Remaining', 'المتبقي');
  String get reviewRequeued =>
      _('This word will come back', 'ستعود هذه الكلمة مرة أخرى');
  String get weeklyScore => _('Weekly score', 'نتيجة الأسبوع');
  String get firstPassCorrect =>
      _('Correct on first attempt', 'صحيحة من المحاولة الأولى');
  String get reviewDoesNotChange => _(
        'This review measures memory. It does not change any word\'s skill state.',
        'هذه المراجعة تقيس التذكّر فقط ولا تغيّر حالة أي كلمة.',
      );

  // Settings
  String get settings => _('Settings', 'الإعدادات');
  String get profile => _('Profile', 'الملف الشخصي');
  String get skillLevels => _('Skill levels', 'مستويات المهارات');
  String get yourLevel => _('Your choice', 'اختيارك');
  String get systemLevel => _('System-validated', 'المستوى المُثبت من النظام');
  String get levelExplainer => _(
        'Changing your level changes the difficulty of generated content only. Progression and archiving follow the level WordOS has validated from your performance.',
        'تغيير مستواك يغيّر صعوبة المحتوى فقط. أما التقدّم والأرشفة فيعتمدان على المستوى الذي أثبته أداؤك.',
      );
  String get spellingNotLevelled => _(
        'Measured, but not a CEFR level',
        'تُقاس، لكنها ليست مستوى CEFR',
      );
  String get dailyTargets => _('Daily word targets', 'الهدف اليومي للكلمات');
  String get dailyTargetsExplainer => _(
        'Each skill can carry a different load.',
        'يمكن أن يختلف العدد من مهارة إلى أخرى.',
      );
  String get interests => _('Interests', 'الاهتمامات');
  String get appearance => _('Appearance', 'المظهر');
  String get themeSystem => _('System', 'النظام');
  String get themeLight => _('Light', 'فاتح');
  String get themeDark => _('Dark', 'داكن');
  String get language => _('Language', 'اللغة');
  String get developerTools => _('Developer tools', 'أدوات المطوّر');

  // ── Feedback (ADR-053) ─────────────────────────────────────────────────────
  String get feedbackSection => _('Help', 'المساعدة');
  String get feedbackTitle =>
      _('Message the team', 'راسل فريق التطبيق');
  String get feedbackHint => _(
        'Report a problem or suggest something',
        'أبلغ عن مشكلة أو اقترح شيئًا',
      );
  String get feedbackPrompt => _(
        'Tell us what happened. We read every message.',
        'اكتب لنا ما حدث. نقرأ كل رسالة.',
      );
  String get feedbackPlaceholder => _(
        'What went wrong, or what would you like to see?',
        'ما الذي حدث؟ أو ما الذي تودّ رؤيته؟',
      );
  String get feedbackSend => _('Send', 'إرسال');
  String get feedbackSending => _('Sending…', 'جارٍ الإرسال…');
  String get feedbackSent =>
      _('Sent — thank you.', 'تم الإرسال — شكرًا لك.');
  String get feedbackFailed => _(
        'That could not be sent. Try again in a moment.',
        'تعذّر الإرسال. حاول بعد لحظة.',
      );

  String get supportTitle =>
      _('Contact support', 'تواصل مع الدعم');
  String get supportHint => _(
        'Chat with the developer on WhatsApp',
        'محادثة مع المطوّر عبر واتساب',
      );
  String get supportUnavailable => _(
        'WhatsApp could not be opened. You can reach us on this number:',
        'تعذّر فتح واتساب. يمكنك التواصل معنا على هذا الرقم:',
      );

  // The Owner's side of it.
  String get devFeedback => _('Feedback', 'الملاحظات');
  String get devFeedbackTitle => _('User feedback', 'ملاحظات المستخدمين');
  String get devFeedbackHint => _(
        'What learners have written, unread first.',
        'ما كتبه المتعلمون، غير المقروء أولًا.',
      );
  String get devFeedbackEmpty =>
      _('No messages yet.', 'لا توجد رسائل بعد.');
  String devFeedbackUnread(int count) => _(
        '$count unread',
        '$count غير مقروءة',
      );
  String get devFeedbackMarkHandled => _('Mark handled', 'تمّت المعالجة');
  String get devFeedbackMarkNew => _('Mark unread', 'أعِدها غير مقروءة');
  String get devFeedbackHandled => _('Handled', 'مُعالجة');
  String get devContact => _('Contact', 'التواصل');
  String get devNoPhone => _('No number given', 'لم يُدخل رقمًا');
  String get devCopied => _('Copied', 'تم النسخ');

  // Owner / developer dashboard
  String get developerDashboard =>
      _('Developer Dashboard', 'لوحة تحكم المطوّر');
  String get devEntryHint => _(
        'Users, analytics and algorithm health',
        'المستخدمون والتحليلات وصحّة الخوارزمية',
      );
  // The dashboard's reporting window (Part 3).
  String get devRangeAllTime => _('All time', 'كل الوقت');
  String get devRangeToday => _('Today', 'اليوم');
  String devRangeDays(int days) => _('$days days', '$days أيام');
  String get devRangeCustom => _('Custom…', 'مخصص…');
  String get devRangeCustomTitle =>
      _('How many days?', 'كم عدد الأيام؟');
  String devShowingRange(String range) =>
      _('Showing: $range', 'المعروض: $range');
  String get devSearchUsers =>
      _('Search by name or email', 'ابحث بالاسم أو البريد');
  /// Accounts, not learners.
  ///
  /// This list shows every account including the Owner's own, while the
  /// overview counts learners alone — so calling both "learners" made two
  /// screens disagree about the same word (185 here, 180 there).
  String devUsersFound(int count) => _(
        count == 1 ? '1 account' : '$count accounts',
        count == 1 ? 'حساب واحد' : '$count حساب',
      );
  String get devNoUsersMatch =>
      _('No learners match', 'لا يوجد متعلّمون مطابقون');
  // Placement evidence (Part 3): the answers behind a level, and where the
  // learner started against where they are now.
  String get devPlacementEvidence =>
      _('Placement evidence', 'أدلة تحديد المستوى');
  String get devInitialVsCurrent =>
      _('Started at → now', 'البداية ← الآن');
  String get devInitialVsCurrentHint => _(
        'The band placement assigned, beside the level the system has since '
        'validated from real sessions.',
        'المستوى الذي حدده الاختبار، بجانب المستوى الذي تحقّق منه النظام من '
        'الجلسات الفعلية.',
      );
  String get devPlacementAnswers => _('Answers', 'الإجابات');
  String get devPlacementAnswersHint => _(
        'Every item asked, with what the learner actually answered.',
        'كل سؤال طُرح، مع ما أجاب به المتعلّم فعليًا.',
      );
  String devTestVersion(int version) =>
      _('Test version $version', 'إصدار الاختبار $version');
  String devFallbackScored(int count) => _(
        '$count scored offline',
        'صُحّحت $count دون الذكاء الاصطناعي',
      );
  String devAlsoEvidenceFor(String skill) =>
      _('Also evidence for $skill', 'دليل أيضًا على $skill');
  String get devNoPlacement =>
      _('No placement test on record', 'لا يوجد اختبار تحديد مستوى مسجّل');
  String get devOverview => _('Overview', 'نظرة عامة');
  String get devUsers => _('Users', 'المستخدمون');
  String get devUserCount => _('Learners', 'المتعلمون');
  String get devActiveToday => _('Active today', 'نشطون اليوم');
  String devActiveThisWeek(int count) =>
      _('$count this week', '$count هذا الأسبوع');
  String get devAvgWordsPerDay =>
      _('Words / learner / day', 'كلمات لكل متعلم يوميًا');
  String devWordsTotal(int count) => _('$count total', '$count إجمالًا');
  String get devAvgSessions => _('Sessions / learner', 'جلسات لكل متعلم');
  /// The typical session, formatted as a person would say it.
  ///
  /// Was the mean, printed in raw seconds — which read "avg 2716s" for a
  /// population whose middle session is sixteen seconds long.
  String devTypicalDuration(int seconds) {
    final text = seconds >= 60
        ? _('${seconds ~/ 60}m ${seconds % 60}s', '${seconds ~/ 60}د ${seconds % 60}ث')
        : _('${seconds}s', '$secondsث');
    return _('typical $text', 'المعتاد $text');
  }
  String get devPipelineCompletion =>
      _('Pipeline completion', 'إكمال المسار');
  String get devPipelineCompletionHint =>
      _('added → Active', 'من الإضافة إلى النشطة');
  String get devAiFallback => _('AI fallback rate', 'نسبة بديل الذكاء');
  String get devAiFallbackHint =>
      _('scored without AI', 'قُيّمت بدون ذكاء اصطناعي');
  String get devPassRate => _('Pass rate by skill', 'نسبة النجاح لكل مهارة');
  String get devPassRateHint => _(
        'A skill far below the others usually means the task is mistuned, not that learners are weak.',
        'انخفاض مهارة كثيرًا عن البقية يعني غالبًا أن التمرين غير مضبوط، لا أن المتعلمين ضعفاء.',
      );
  String get devFirstAttempt =>
      _('First-attempt accuracy', 'دقة المحاولة الأولى');
  String get devFirstAttemptHint => _(
        'Of the words this skill has decided, the share that passed without a retry.',
        'من الكلمات التي حُسم أمرها في هذه المهارة، نسبة ما نجح دون إعادة.',
      );
  String get devFailureDistribution =>
      _('Where failures land', 'أين تتركز الإخفاقات');
  String get devFailureDistributionHint => _(
        'Share of all failures attributable to each skill.',
        'نصيب كل مهارة من إجمالي الإخفاقات.',
      );
  String get devLevelDistribution =>
      _('Level distribution', 'توزيع المستويات');
  String get devLevelDistributionHint => _(
        'System-validated levels only. Spelling is measured but never levelled.',
        'المستويات المُثبتة من النظام فقط. التهجئة تُقاس ولا تُصنَّف بمستوى.',
      );
  String get devTopInterests => _('Interests', 'الاهتمامات');
  String get devTopInterestsHint => _(
        'Marked ✨ = typed by a learner, not in our catalogue yet.',
        'المعلَّمة ✨ كتبها المتعلمون ولم تُدرَج في القائمة بعد.',
      );
  String get devOwnerRole => _('Owner', 'مالك');
  String devUserRowSummary(int words, int active, int sessions) => _(
        '$words words · $active active · $sessions sessions done',
        '$words كلمة · $active نشطة · $sessions جلسة مكتملة',
      );
  String devLastActive(String when) =>
      _('Last active $when', 'آخر نشاط $when');
  String get devNeverActive => _('Never active', 'لا يوجد نشاط');
  String get devToolsUnavailable =>
      _('Not available on the real backend', 'غير متاح مع الخادم الحقيقي');
  String get devToolsUnavailableBody => _(
        'Time travel only exists in the development mock.',
        'السفر عبر الزمن موجود فقط في النسخة التجريبية.',
      );

  // Owner → user detail
  String get devAccount => _('Account', 'الحساب');

  // ── The time skip (ADR-037) ────────────────────────────────────────────────
  String get devTimeTravel => _('Testing tools', 'أدوات الاختبار');
  String get devSkipDays => _('Skip 2 days', 'تقديم يومين');
  String get devSkipDaysHint => _(
        'The pipeline waits two days between one skill and the next. This brings '
        'this learner\'s waiting skills forward so the gap can be tested without '
        'waiting — nothing that already happened changes, and the skip is recorded.',
        'يفصل المسار يومين بين مهارة وأخرى. هذا يقدّم المهارات المنتظرة لهذا '
        'المتعلّم حتى تُختبر الفجوة دون انتظار — لا يتغيّر شيء مما حدث فعلًا، '
        'والتقديم يُسجَّل.',
      );
  String devSkipDaysDone(int days, int due) => _(
        'Moved $days days forward — $due skills are available now.',
        'تم التقديم $days يومين — $due مهارة متاحة الآن.',
      );
  String get devJoined => _('Joined', 'تاريخ الانضمام');
  String get devLastActiveLabel => _('Last active', 'آخر نشاط');
  String get devSignIns => _('Sign-ins', 'مرات الدخول');
  String get devVocabularyHint =>
      _('Lifecycle and recent additions.', 'دورة الحياة والإضافات الأخيرة.');
  String get devToday => _('Today', 'اليوم');
  String get devThisWeek => _('This week', 'هذا الأسبوع');
  String get devThisMonth => _('This month', 'هذا الشهر');
  String get devLevelsHint => _(
        'Chosen level and system-validated level are shown separately — the gap between them is the signal.',
        'يُعرض المستوى المختار والمستوى المُثبت منفصلين، والفارق بينهما هو المؤشر.',
      );
  String get devSkillPerformance =>
      _('Performance by skill', 'الأداء لكل مهارة');
  String get devSkillPerformanceHint => _(
        'Sessions completed, and words passed versus failed.',
        'الجلسات المكتملة والكلمات الناجحة مقابل الفاشلة.',
      );
  /// Three numbers that do not add up, because they are not the same thing:
  /// a session covers several words, so sessions can never be the sum of the
  /// two word counts beside them. The unit is now said out loud.
  String devSkillStatLine(int sessions, int passed, int failed) => _(
        '$sessions sessions · words: $passed passed, $failed failed',
        '$sessions جلسة · الكلمات: $passed ناجحة، $failed فاشلة',
      );
  String get devDaily => _('Day by day', 'يومًا بيوم');
  String get devDailyHint => _(
        'Faded columns are days with no sign-in.',
        'الأعمدة الباهتة أيام بلا تسجيل دخول.',
      );
  String get devDailyWordsAdded => _('Words added', 'الكلمات المضافة');
  String get devDailySessions => _('Skill attempts', 'محاولات المهارات');
  String get devMistakes => _('Words got wrong', 'الكلمات الخاطئة');
  String get devMistakesHint => _(
        'A wrong answer never deletes a word — it schedules it again.',
        'الإجابة الخاطئة لا تحذف الكلمة، بل تعيد جدولتها.',
      );
  String get devNoMistakes => _('No mistakes recorded', 'لا أخطاء مسجلة');
  String get devLevelHistory => _('Level history', 'سجل المستويات');
  String get devLevelHistoryHint => _(
        'A wide gap between what the learner chose and what the system proved is the interesting signal.',
        'الفارق الكبير بين ما اختاره المتعلم وما أثبته النظام هو المؤشر المهم.',
      );
  String get devNoLevelChanges =>
      _('No level changes yet', 'لا تغييرات في المستوى بعد');
  String devSystemChanges(int count) =>
      _('$count system-validated', '$count مُثبت من النظام');
  String devManualChanges(int count) =>
      _('$count manual', '$count يدوي');
  String get devMastered => _('Mastered (Active)', 'متقنة (نشطة)');
  String get devMasteredHint => _(
        'Passed all five skills.',
        'اجتازت المهارات الخمس.',
      );
  String get devNoneYet => _('None yet', 'لا شيء بعد');
  String get timeTravel => _('Time travel', 'السفر عبر الزمن');
  String get timeTravelExplainer => _(
        'Mock backend only: skip ahead to see words become due after the skill gap.',
        'للخادم التجريبي فقط: تقدّم بالوقت لترى الكلمات تصبح مستحقة بعد الفاصل الزمني.',
      );
  String get advanceTwoDays => _('Skip 2 days', 'تقديم يومين');
  String get resetClock => _('Reset clock', 'إعادة ضبط الوقت');
  String clockOffsetLabel(int days) =>
      _('Clock is $days days ahead', 'الوقت متقدّم $days يومًا');
}

/// Selected UI locale.
///
/// Defaults to **Arabic** and persists an explicit choice from Settings. The
/// device locale is deliberately ignored: the current audience is Arabic-
/// speaking, so an English phone must still open the app in Arabic until the
/// learner says otherwise (demo review §2).
class LocaleController extends StateNotifier<Locale> {
  LocaleController(this._prefs) : super(_prefs.locale);

  final AppPreferences _prefs;

  Future<void> setLocale(Locale locale) async {
    if (locale == state) return;
    state = locale;
    await _prefs.setLocale(locale);
  }
}

final localeProvider = StateNotifierProvider<LocaleController, Locale>(
  (ref) => LocaleController(ref.watch(appPreferencesProvider)),
);

final stringsProvider =
    Provider<AppStrings>((ref) => AppStrings(ref.watch(localeProvider)));
