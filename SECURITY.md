# Bezpieczeństwo WorkRouter for Windows

## Zakres

Zgłaszaj podatności dotyczące kodu, skryptów instalacyjnych, usługi Windows, launchera, panelu, tokenów sesji, WFP, udziału SMB i ochrony danych konfiguracyjnych.

Przed zgłoszeniem sprawdź, czy problem nie wynika z polityki firmowego laptopa, VPN/EDR albo ograniczeń opisanych w sekcji telemetrii README. Brak widoczności ruchu przez ICS, DoH, ECH, DoT, QUIC lub VPN jest znanym ograniczeniem, nie samodzielną podatnością.

## Zgłaszanie prywatne

Nie publikuj szczegółów podatności w publicznym issue, pull requeście, zrzucie ekranu ani logu. Po opublikowaniu repozytorium użyj funkcji **GitHub Security Advisories** i utwórz prywatny raport dla maintainerów. Do czasu publikacji repozytorium skontaktuj się z osobą utrzymującą projekt przez uzgodniony prywatny kanał organizacji; ten plik celowo nie zawiera publicznego adresu kontaktowego.

Raport powinien zawierać:

- wersję lub commit, którego dotyczy problem;
- minimalny, powtarzalny opis kroków;
- wpływ na poufność, integralność lub dostępność;
- bezpieczny dowód koncepcji, jeśli jest konieczny;
- informację, czy problem występuje tylko z uprawnieniami administratora albo na fizycznym sprzęcie.

Nie dołączaj prawdziwych haseł, tokenów, nazw użytkowników, nazw hostów, adresów sieciowych, plików firmowych ani pełnych capture’ów. Zastąp je syntetycznymi wartościami.

## Obsługa zgłoszeń

Maintainer potwierdza odbiór, ocenia wpływ, przygotowuje poprawkę i ustala skoordynowane ujawnienie. Nie uruchamiaj publicznych testów penetracyjnych na komputerze użytkownika ani na sieci firmowej bez wyraźnej zgody właściciela.

## Zasady bezpiecznego użycia

- instaluj tylko pakiety ze zweryfikowanym manifestem SHA‑256;
- uruchamiaj instalację, zmiany WFP i udziału z uprawnieniami administratora;
- otwieraj panel przez launcher, który dostarcza token sesji;
- nie wystawiaj lokalnego API na interfejs Ethernet ani Wi‑Fi;
- nie zapisuj hasła Wi‑Fi/SMB, tokenu ani danych telemetrii w repozytorium;
- sprawdź zachowanie z firmowym VPN/EDR i respektuj politykę organizacji;
- przed odinstalowaniem wykonaj kopię danych udziału, ale nie kopię sekretów do publicznej lokalizacji.
