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
  String get finish => _('Finish', 'إنهاء');
  String get start => _('Start', 'ابدأ');
  String get skip => _('Skip', 'تخطي');
  String get somethingWentWrong =>
      _('Something went wrong', 'حدث خطأ ما');
  String get loading => _('Loading…', 'جارٍ التحميل…');

  // Auth
  String get signIn => _('Sign in', 'تسجيل الدخول');
  String get signUp => _('Create account', 'إنشاء حساب');
  String get signOut => _('Sign out', 'تسجيل الخروج');
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
  String get passwordRequired =>
      _('Password must be at least 6 characters', 'كلمة المرور 6 أحرف على الأقل');
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
  String get placementResultTitle =>
      _('Your starting levels', 'مستوياتك الأولية');
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
  String get typeWord => _('Type an English word', 'اكتب كلمة إنجليزية');
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
  String get listenToSentence => _('Listen to this sentence', 'استمع إلى هذه الجملة');
  String get audioUnavailable => _(
        'Audio is unavailable on this device — here is the sentence instead',
        'الصوت غير متاح على هذا الجهاز — إليك الجملة بدلًا من ذلك',
      );
  String get showHint => _('Need a hint?', 'تحتاج تلميحًا؟');
  String spellingClueLabel(SpellingClueKind? kind) => switch (kind) {
        SpellingClueKind.definitionEn => _('Definition', 'التعريف'),
        SpellingClueKind.synonym => _('Similar word', 'كلمة مرادفة'),
        _ => _('Meaning', 'المعنى'),
      };
  String get correct => _('Correct', 'إجابة صحيحة');
  String get incorrect => _('Not quite', 'إجابة غير صحيحة');
  String get correctAnswerIs => _('Correct answer', 'الإجابة الصحيحة');
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
  String get typeInstead => _('Type instead', 'اكتب بدلًا من ذلك');
  String get useVoice => _('Use voice', 'استخدم الصوت');
  String get replayTurn => _('Say that again', 'أعد ما قلته');
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

  // Owner / developer dashboard
  String get developerDashboard =>
      _('Developer Dashboard', 'لوحة تحكم المطوّر');
  String get devEntryHint => _(
        'Users, analytics and algorithm health',
        'المستخدمون والتحليلات وصحّة الخوارزمية',
      );
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
  String devAvgDuration(int seconds) =>
      _('avg ${seconds}s', 'المتوسط $secondsث');
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
        'Share of words passed without needing a retry.',
        'نسبة الكلمات التي نجحت دون إعادة.',
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
        '$words words · $active active · $sessions sessions',
        '$words كلمة · $active نشطة · $sessions جلسة',
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
  String devSkillStatLine(int sessions, int passed, int failed) => _(
        '$sessions sessions · $passed passed · $failed failed',
        '$sessions جلسة · $passed ناجحة · $failed فاشلة',
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
