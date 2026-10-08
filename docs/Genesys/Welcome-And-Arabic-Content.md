# Welcome message and Arabic content pack (DRAFT — needs business approval)

**Where this lives:** the chat/voice greeting, language menu and prompts are **configured in Genesys
Architect / the voice flow**. TigerCS returns no greeting text and no localised text on any
`/api/genesys` route (only data and English `ProblemDetails` text meant for flow logic). So this page
is content for Genesys, not a TigerCS feature. TigerCS API field names stay English and stable.

Brand name: the requirement says "Tiger Group"; current customer emails say "Tiger Properties"
(decision D3 in `docs/releases/UAT-Outstanding-Requirements-Readiness.md`). Arabic text was drafted
in Modern Standard Arabic and must be reviewed by a native Arabic speaker before use.

## Services that may be advertised today

Only what is implemented and usable in UAT: **logging a request/inquiry** and **speaking to a
customer service agent**. Do **not** advertise payments/balances, unit or handover details, document
copies, statements of account, construction updates, payment links or reminders — each is blocked or
gated off (see readiness matrix). Add a line to the menu only when that row turns green on UAT.

## Chat (website / messenger) — language selection supported

Step 1 (bilingual, shown before the language is known):

> Welcome to Tiger Group. Please choose your language / أهلاً بكم في مجموعة تايجر، الرجاء اختيار اللغة:
> 1. English  2. العربية

Step 2 — English:

> Hello, I'm Tiger Group's virtual assistant. I can log your request or inquiry with our Customer Service team, or connect you to a customer service agent. How can I help you today?

Step 2 — Arabic:

> مرحباً بكم، أنا المساعد الافتراضي لمجموعة تايجر. يمكنني تسجيل طلبكم أو استفساركم لدى فريق خدمة العملاء، أو تحويلكم إلى أحد موظفي خدمة العملاء. كيف يمكنني مساعدتكم اليوم؟

## Voice — language selection by keypad (assumed; depends on the voice flow)

> Welcome to Tiger Group. For English, press 1. للعربية، اضغط ٢.

Then the Step 2 text for the chosen language, read by the approved TTS voice for that language.
Whether the Genesys voice flow has an Arabic TTS voice is a Genesys configuration dependency.

## Standard messages (Arabic equivalents of flow-side wording)

| Situation | English | Arabic |
|---|---|---|
| Request logged | Your request has been logged. Your reference is {ticketNumber}. | تم تسجيل طلبكم. رقم المرجع: {ticketNumber}. |
| Handing to an agent | I'll connect you with a customer service agent now. | سأقوم بتحويلكم إلى أحد موظفي خدمة العملاء الآن. |
| No agent available | No agent is available right now. Your request is saved and the team will contact you. | لا يوجد موظف متاح حالياً. تم حفظ طلبكم وسيتواصل معكم الفريق. |
| Legal question (never answer) | I can't provide legal advice. I'll connect you with a team member who can help. | لا يمكنني تقديم استشارة قانونية. سأحوّلكم إلى أحد موظفينا للمساعدة. |
| Service unavailable | This information is not available right now. I can connect you with an agent. | هذه المعلومات غير متاحة حالياً. يمكنني تحويلكم إلى أحد موظفينا. |
| Could not verify | I couldn't verify your details, so I can't share account information. I can connect you with an agent. | تعذّر التحقق من بياناتكم، لذلك لا يمكنني مشاركة معلومات الحساب. يمكنني تحويلكم إلى أحد موظفينا. |
| Inactivity closure notice (optional) | We haven't heard from you, so we've closed this chat. Contact us any time to continue. | لم نتلقَّ ردّاً منكم، لذلك تم إنهاء المحادثة. يمكنكم التواصل معنا في أي وقت للمتابعة. |

## Arabic audit — what remains outside this content

| Area | State | Owner |
|---|---|---|
| Bot/voice prompts, menus, errors | Content above; to be entered in Architect | Genesys |
| `/api/genesys` error `title`/`detail` | English, for flow logic — the flow should map `code`/status to the Arabic text above, never read them to customers | Genesys flow |
| Customer ticket emails (received/resolved/closed/reopened) | English only, `lang="en"`, no `dir` | TigerCS — needs approved Arabic templates + language source (D5) |
| Document-copy email | English only | TigerCS — same |
| Collections reminder email | `language=ar` is accepted and stored but **refused at dispatch** (no approved template) | TigerCS after wording approval |
| Customer names | `fullNameArabic` and project `arabicName` are passed through when CRM holds them | CRM data |
| Staff Web UI RTL | Not implemented (`lang="en"`, no RTL CSS) | D5 |
