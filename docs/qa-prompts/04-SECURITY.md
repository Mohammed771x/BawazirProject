# 04 — الأمن والخصوصية

طبّق `00-COMMON.md`. هذا تقييم دفاعي للمشروع وبيئة الاختبار المصرح بها. افحص الفرضيات وأثبت النجاح أو العيب؛ لا تعتبر checklist أو scanner verdict تقرير اختراق مثبتًا. ابدأ بفحص ساكن ثم اختبارات محدودة قابلة للإعادة.

**اقرأ:** `docs/07-SECURITY.md` باعتباره وثيقة قديمة تحتاج مطابقة، `Program.cs`، auth/reset و`Policies` و`ClientAddress`، طبقة Security وPersistence، token store وAPI clients، `Admin/AgentTools.cs` و`AdminAgentEndpoints.cs`، إعداد Python وصلاحية service token، manifests وDocker وملفات الاعتمادات. لا تطبع `.env` أو user-secrets.

## نطاق التحقق

1. مصفوفة Anonymous/User A/User B/Analyst/Owner لكل API حساس. بدّل IDs للكلمات والجلسات والـplacement والمراجعات وfeedback ومحادثات AI؛ اختبر list/detail/mutate/export إن وجدت. تحقق من عزل المالك الفعلي في DB والاستجابات، لا إخفاء الزر فقط. Analyst يرى ما تسمح به السياسة دون بيانات اتصال أو تصعيد للقرار؛ الطلب العادي لا يستطيع إنشاء Owner.
2. JWT: توقيع/issuer/audience/lifetime/clock skew، تعديل claims، عدم قبول token من بيئة أخرى. refresh rotation/replay/revocation مع grace 60 ثانية الافتراضية والتزامن؛ logout/reset وما يبقى من عمر access token، مع تقييمه مقابل السياسة الفعلية لا افتراض إبطال JWT فوريًا.
3. كلمات المرور Argon2id وإعداداته وصيغته، المقارنة، عدم تسريب hashes؛ reset code أحادي الاستخدام والانتهاء والمحاولات والإلغاء عند إصدار جديد. قارن السلوك والوقت لحساب موجود وغير موجود بعينات مناسبة؛ لا تدّعِ enumeration من فرق قياس واحد.
4. Injection وmass assignment وتجاوز workflow: SQL/HTML/Markdown/URL/Unicode وحقول level/role/passed/userId المزوّرة. تحقق أن اختيار sense يعيد الحل على الخادم، وأن الإجابة الصحيحة ومفاتيح التصحيح لا تُسرّب قبل وقتها عبر DTO أو logs.
5. XSS في لوحة الإدارة عبر feedback/اسم/معنى/AI، الروابط والبروتوكولات الضارة وMarkdown renderer. CSP وframing وCORS وCSRF بحسب آلية المصادقة الفعلية. sessionStorage قابل للقراءة بواسطة JS؛ قيّم خطر XSS ولا تصفه بأنه HttpOnly أو آمن لمجرد غلق التبويب.
6. MASVS: secure storage، بقايا tokens بعد الخروج، cache/ملفات الصوت والنسخ الاحتياطي، logs/crash dumps، الأذونات وdeep links، release مقابل debug، HTTPS. غياب certificate pinning قرار موثق؛ لا ترفعه لمانع نشر دون threat model يبرر ذلك.
7. أسرار المصدر وتاريخه والـbundles/image وCI artifacts بواسطة فحص يحجب النتائج؛ dependencies وSBOM وثغرات مثبتة الصلة بالإصدار والوصول، لا كل CVE عابر في أداة التطوير. لا تختبر مفتاحًا مشتبهًا باستعماله خارجيًا.
8. الخدمة الداخلية على loopback وX-Service-Token، رفض غيابه وخطئه، عدم شحن `AI_ALLOW_UNAUTHENTICATED` أو mock في الإنتاج. DB runtime role مقابل migrator، منع DDL، parameterization وأخطاء بلا SQL/stack/secrets. اختبر SSRF فقط إذا وجدت مسار URL قابلًا للتحكم، ولا تنفذ طلبات نحو metadata حقيقية.
9. الخصوصية: ما يصل إلى Gemini/Groq/الصوت وما يُخزّن وما يسجل. ميّز إخفاء بيانات المتعلم عن **أدوات محلل الإدارة** عن مسارات تدريس قد تحمل اسمًا أو كتابة. تحقق من إزالة PII من tool outputs وسياق المحادثة، وعدم خلط حسابات. قارن حذف الكلمة المنطقي بأي متطلب مستقل لحذف الحساب/البيانات؛ لا تساوِ بينهما.
10. موارد/تكلفة: نسّق rate-limit مع 08 وAI budget مع 05/10. لا تنفذ DDoS أو تستنزف مزودًا لإثبات حد.

اربط الفحوص المناسبة بإصدار محدد من [ASVS](https://asvs.owasp.org/) و[API Top 10](https://owasp.org/API-Security/editions/2023/en/0x11-t10/) و[MASVS/MASTG](https://mas.owasp.org/MASTG/). وضّح غير المنطبق. استعمل ASVS Level 2 كنطاق مقترح ملائم، لا ادعاء شهادة امتثال.

التسليم: threat model موجز وتدفق بيانات، مصفوفة الصلاحيات والأدلة، نتائج الأدوات بعد triage، PoC محلي محدود لكل عيب مؤكد مع الأثر والتصنيف. إن لم تظهر عيوب، صرّح بنجاح الفحوص المنفذة دون ضمان مطلق.
