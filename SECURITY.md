# Bezpieczeństwo WorkRouter for Windows

## Zakres

Polityka obejmuje kod, skrypty instalacyjne, usługę Windows, launcher, panel, tokeny sesji, WFP, udział SMB oraz ochronę danych konfiguracyjnych. Dotyczy także procesu budowania i publikowania artefaktów.

Brak widoczności ruchu przez ICS, DoH, ECH, DoT, QUIC lub VPN jest znanym ograniczeniem telemetrii i nie stanowi samodzielnie podatności. Zgłoś problem, jeśli ograniczenie prowadzi do błędnego zezwolenia na ruch, ujawnienia sekretu albo nieprawdziwego stanu bezpieczeństwa.

## Wspierane wersje

| Wersja | Status poprawek bezpieczeństwa |
| --- | --- |
| `0.1.x` | wspierana w ramach bieżącej linii; poprawki są publikowane według wpływu i możliwości maintainerów |
| `Unreleased` | gałąź rozwojowa; może zawierać niezałatane problemy i nie jest wydaniem produkcyjnym |
| `<0.1.0`, prywatne forki i lokalne modyfikacje | niewspierane; odtwórz problem na wspieranej wersji |

Wydanie naprawcze może wymagać aktualizacji całej linii. Użytkownik powinien sprawdzić changelog i manifest SHA-256 pobranego pakietu.

## Prywatne zgłaszanie

Nie publikuj szczegółów podatności w publicznym issue, pull requeście, zrzucie ekranu ani logu. Użyj funkcji [GitHub Security Advisories](https://github.com/mateuszsury/workrouter-windows/security/advisories/new) i utwórz prywatny raport dla maintainerów. Repozytorium musi mieć włączone prywatne advisories; jeżeli kanał jest chwilowo niedostępny, nie publikuj dowodu publicznie i poczekaj na jego przywrócenie.

Raport powinien zawierać:

- wersję lub commit, którego dotyczy problem;
- minimalny, powtarzalny opis kroków;
- wpływ na poufność, integralność lub dostępność;
- bezpieczny dowód koncepcji, jeśli jest konieczny;
- informację, czy problem występuje tylko z uprawnieniami administratora, VPN/EDR albo na fizycznym sprzęcie;
- ocenę, czy problem można odtworzyć na bieżącej wersji `0.1.x`.

Nie dołączaj prawdziwych haseł, tokenów, kluczy prywatnych, nazw użytkowników, nazw hostów, adresów sieciowych, plików firmowych ani pełnych capture’ów. Zastąp je syntetycznymi wartościami, zanonimizuj domeny i usuń dane osobowe z załączników. Jeśli sekret został ujawniony, natychmiast go unieważnij/obróć i zgłoś jedynie fakt ekspozycji.

## Terminy reakcji

To cele operacyjne, a nie gwarancja naprawy w określonym terminie:

- potwierdzenie odbioru: do 5 dni roboczych;
- wstępna kwalifikacja wpływu i zakresu: do 10 dni roboczych;
- aktualizacja statusu dla aktywnego zgłoszenia: co najmniej co 14 dni;
- cel remediacji: do 30 dni dla krytycznych, 60 dni dla wysokich, 90 dni dla średnich; niskie trafiają do najbliższego rozsądnego wydania.

Termin może się wydłużyć z powodu zależności Windows, sterownika, konieczności testu fizycznego lub braku reprodukcji. W takim przypadku advisory otrzyma aktualizację z uzasadnieniem i planem dalszych działań.

## Skoordynowane ujawnienie

Maintainer przygotowuje poprawkę lub środek ograniczający, ocenia potrzebę CVE/GitHub advisory i uzgadnia z reporterem termin publikacji. Domyślny cel publikacji to 90 dni od dostępnej poprawki albo wcześniej, gdy problem jest aktywnie wykorzystywany. Reporter może otrzymać kredyt w advisory, jeśli wyrazi zgodę. Nie publikuj exploita, zanim poprawka lub uzgodnione ograniczenie nie będzie dostępne.

## Co dzieje się po zgłoszeniu

Maintainer potwierdza odbiór, próbuje odtworzyć problem w izolowanym środowisku, ustala wpływ i zależności, a następnie prowadzi prywatny wątek advisory. Po naprawie wykonywane są testy regresji oraz, gdy potrzeba, test na fizycznym Windows/ICS/WFP. Status może pozostać `NOT_RUN`, dopóki wymagany test nie zostanie wykonany.

Nie uruchamiaj publicznych testów penetracyjnych na komputerze użytkownika ani na sieci firmowej bez wyraźnej zgody właściciela.

## Zasady bezpiecznego użycia

- instaluj tylko pakiety ze zweryfikowanym manifestem SHA-256 i pochodzeniem opisanym w [procesie wydań](RELEASE.md);
- uruchamiaj instalację, zmiany WFP i udziału z uprawnieniami administratora;
- otwieraj panel przez launcher, który dostarcza token sesji;
- nie wystawiaj lokalnego API na interfejs Ethernet ani Wi‑Fi;
- nie zapisuj hasła Wi‑Fi/SMB, tokenu ani danych telemetrii w repozytorium;
- sprawdź zachowanie z firmowym VPN/EDR i respektuj politykę organizacji;
- przed odinstalowaniem wykonaj kopię danych udziału, ale nie kopię sekretów do publicznej lokalizacji.

## Podziękowania

Zgłaszający może zostać wymieniony w publicznym advisory wyłącznie za jego zgodą. Nie publikujemy danych kontaktowych ani tożsamości bez wyraźnej prośby.
