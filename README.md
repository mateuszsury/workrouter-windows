# WorkRouter for Windows

WorkRouter to lokalna usługa dla Windows 11, która udostępnia połączenie Ethernet przez odseparowany hotspot Wi‑Fi `WORK`. Laptop firmowy dostaje Internet i dostęp do jednego udziału SMB, ale ruch do prywatnych zakresów sieci domowej jest filtrowany przez Windows Filtering Platform (WFP).

To narzędzie administracyjne do kontrolowanego użycia na jednym komputerze. Nie jest zamiennikiem firmowego firewalla, MDM, VPN ani polityki bezpieczeństwa organizacji.

## Cel i granice

WorkRouter rozwiązuje scenariusz, w którym komputer-host ma jednocześnie:

- uplink Ethernet do Internetu;
- kartę Wi‑Fi zdolną do pracy jako Windows Mobile Hotspot;
- folder przeznaczony do wymiany plików z laptopem firmowym.

Segment WORK jest osobną siecią za NAT-em Windows. WFP dodatkowo blokuje ruch z WORK do prywatnych zakresów IPv4 i cały przekazywany IPv6. Na hoście dopuszczony jest tylko zakres usług potrzebny do działania hotspotu i udziału: DHCP, DNS oraz SMB na TCP 445. Klienci WORK są obecnie traktowani jako zaufani względem siebie i mogą się wzajemnie widzieć; izolacja klient‑klient nie jest obiecywana.

Przykładowa prywatna podsieć WORK to `192.168.137.0/24`. Rzeczywisty adres bramy i zakres wybiera Windows i należy je odczytać z panelu, a nie wpisywać na podstawie tego przykładu.

## Architektura

```text
Internet
   │
Router domowy ── Ethernet ── komputer-host WorkRouter
                                  ├─ WFP / NAT / SMB
                                  └─ Wi‑Fi WORK
                                         └─ laptop firmowy
```

Główne komponenty rozwiązania:

- `WorkRouter.Core` — orkiestracja hotspotu, WFP, udziału SMB, monitoringu użycia i telemetrii;
- `WorkRouter.Service` — usługa Windows, lokalne API HTTP i panel operatorski;
- `WorkRouter.Launcher` — proces w zasobniku, który pobiera krótkotrwały bilet i otwiera panel;
- `tests/WorkRouter.Core.Tests` — testy jednostkowe logiki core;
- `scripts/` — budowanie, instalacja, walidacja klienta i odinstalowanie.

Usługa nasłuchuje domyślnie wyłącznie na pętli zwrotnej. API wymaga tokenu sesji przechowywanego w chronionym pliku stanu. Panel powinien być otwierany z launchera lub skrótu WorkRouter, nie przez ręczne wystawianie portu na sieć.

## Funkcje

- uruchamianie i zatrzymywanie hotspotu z kontrolą stanu;
- fail‑closed aktywacja: udział SMB, kwarantanna WFP, hotspot, aktywna polityka WFP, a dopiero potem zdjęcie kwarantanny;
- watchdog, który zatrzymuje router po utracie wymaganej ochrony;
- konfiguracja SSID, pasma 2,4/5 GHz, limitu klientów i hasła;
- dedykowany udział SMB `Firmowe` wskazujący na skonfigurowany folder, z osobnym kontem technicznym i ograniczonymi ACL;
- hasło udziału synchronizowane z hasłem Wi‑Fi; dokumentacja i logi nie zawierają jego wartości;
- panel po polsku: stan bramek, klienci, transfer, ustawienia, diagnostyka i zdarzenia;
- opcjonalne otwieranie panelu po zalogowaniu oraz automatyczny start routera po starcie usługi;
- czyszczenie ulotnej historii telemetrii;
- skrypt walidacji wykonywany z laptopa firmowego.

## Telemetria i ograniczenia widoczności

Panel pokazuje globalny transfer z monitora użycia interfejsu oraz osobno metadane zaobserwowanych przepływów. Wpisy domen i celów nie są pełnym rozliczeniem treści ani pełnym wolumenem pobranych danych — wartości bajtów są rozmiarem obserwowanych próbek/metadanych.

Telemetria może zawierać:

- adres IP, port, protokół, kierunek i czas przepływu;
- nazwę ustaloną z jawnego DNS, korelacji DNS, HTTP Host albo TLS SNI wraz ze źródłem i pewnością;
- liczniki zapytań, klientów i próbek;
- heurystyczne alerty, które są sygnałem do sprawdzenia, a nie diagnozą malware.

Świadomie nie ma:

- MITM, deszyfrowania TLS, pełnych URL-i ani treści HTTPS;
- przechwytywania payloadu lub zapisu pełnych pakietów;
- gwarancji rozpoznania domen przez DoH, ECH, DoT, QUIC/HTTP‑3 lub VPN;
- gwarancji widoczności ruchu przekazywanego przez Windows Internet Connection Sharing — przechwytywanie ICS jest best‑effort i musi być sprawdzone z fizycznym klientem.

Ślad jest ulotny i ograniczony pamięciowo. Po restarcie usługi może zniknąć. Ustawienie retencji ogranicza bieżącą sesję, ale nie tworzy trwałego archiwum.

## Wymagania

Do użycia:

- Windows 11 z aktualnymi składnikami Mobile Hotspot, WFP i SMB;
- uprawnienia administratora podczas instalacji oraz podczas operacji wymagających zmiany sieci, WFP lub udziału;
- aktywne połączenie Ethernet jako upstream;
- karta Wi‑Fi obsługująca Windows Mobile Hotspot;
- folder przeznaczony do udostępnienia, domyślnie konfigurowany jako `E:\Firmowe`;
- Edge, Chrome lub inny lokalny browser do panelu (launcher wybiera zainstalowaną przeglądarkę);
- .NET 8 SDK tylko do budowania ze źródeł — pakiet publikowany jest self‑contained dla `win-x64`.

Laptop firmowy może mieć VPN, EDR lub politykę blokującą lokalny SMB. WorkRouter nie obchodzi takich zasad.

## Instalacja

1. Otwórz PowerShell w katalogu repozytorium.
2. Zbuduj i zweryfikuj pakiet:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1
   ```

   Skrypt uruchamia testy rozwiązania, publikuje usługę i launcher oraz tworzy `SHA256SUMS.txt`. Przy każdym uruchomieniu odtwarza `artifacts\publish`, więc nie trzymaj tam własnych plików.

   Pakiet z lokalnego builda nie jest podpisany certyfikatem Authenticode. Manifest SHA-256 wykrywa zmianę plików po zbudowaniu, ale nie zastępuje podpisu wydawcy. Publiczne wydanie powinno zostać podpisane przed dystrybucją binariów.

3. Uruchom instalator jako administrator:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1 -Launch
   ```

   Instalator weryfikuje manifest SHA‑256, przygotowuje pakiet w katalogu stagingowym, wykonuje atomową podmianę z kopią rollback, rejestruje usługę z opóźnionym startem, zabezpiecza katalog stanu oraz tworzy skróty WorkRouter w menu Start i na pulpicie. Aktualizacja zachowuje `ProgramData`, konfigurację, preferencje i stan folderu udziału; jeśli router działał przed aktualizacją, instalator uruchamia go ponownie dopiero po potwierdzeniu gotowości lokalnego API.

4. Otwórz panel z ikony zasobnika. Launcher otrzymuje z usługi jednorazowy bilet, wymienia go na sesję i otwiera panel lokalnie.
5. W panelu sprawdź Ethernet, stan WFP, udział SMB i aktualny adres bramy. Ustaw SSID/pasmo/hasło, jeśli chcesz zmienić wartości domyślne.
6. Dopiero po przejściu bramek uruchom router i połącz laptop firmowy wyłącznie z `WORK`.

Nie umieszczaj hasła Wi‑Fi, tokenu sesji ani danych firmowych w repozytorium, zgłoszeniach, zrzutach ekranu lub logach. Hasło odczytaj w panelu przez kontrolowane ujawnienie pola hasła.

## Budowanie i testy

Szybki zestaw lokalny:

```powershell
dotnet test .\WorkRouter.sln -c Release
node --check .\src\WorkRouter.Service\wwwroot\app.js
powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1
```

`build-release.ps1` uruchamia `dotnet test`, publikuje usługę i launcher oraz tworzy manifest. Testy nie zastępują walidacji na fizycznym laptopie, testu sterownika Wi‑Fi, sprawdzenia WFP ani próby z firmowym VPN-em.

## Konfiguracja panelu

Sekcja Wi‑Fi pozwala ustawić:

- nazwę SSID;
- pasmo 2,4 GHz albo 5 GHz — zmiana wykonuje kontrolowany restart hotspotu, gdy router działa;
- limit klientów;
- hasło WPA2 o długości 8–63 znaków ASCII.

Sekcja udziału pokazuje ścieżkę, konto techniczne, stan SMB oraz informację, że hasło udziału jest takie samo jak Wi‑Fi. Operacja synchronizacji ponownie stosuje bieżące hasło; panel nie pokazuje osobnego sekretu.

Ustawienia operacyjne obejmują:

- otwieranie panelu po zalogowaniu do Windows — uruchamia interfejs, nie hotspot;
- automatyczny start routera po uruchomieniu usługi;
- włączenie inspekcji metadanych ruchu;
- retencję bieżącej historii;
- czyszczenie historii.

Włączenie inspekcji podczas pracy routera może wymagać kontrolowanego restartu. Pauzowanie ruchu pojedynczego klienta nie jest obsługiwane i panel nie udaje takiej funkcji.

## Walidacja z laptopa firmowego

Po połączeniu z `WORK` uruchom w PowerShell skrypt z repozytorium. Zastąp wartości opisowe rzeczywistą bramą WORK oraz adresami urządzeń, które mają być zablokowane:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\validate-client.ps1 `
  -WorkGateway <adres-bramy-WORK> `
  -HomeTargets <adres-routera>,<adres-NAS>,<adres-usługi-domowej>
```

Skrypt sprawdza Internet HTTPS, SMB do bramy WORK, blokadę wybranych portów w domowym LAN-ie, trasę przez WORK i ostrzega o globalnym IPv6. `-HomePorts` można ograniczyć lub rozszerzyć zgodnie z testowanymi usługami.

Powtórz test:

1. przed połączeniem z firmowym VPN;
2. po zestawieniu firmowego VPN;
3. po restarcie usługi lub komputera;
4. po zmianie pasma albo konfiguracji udziału.

Jeśli VPN/EDR blokuje lokalny SMB, nie próbuj tego omijać — traktuj wynik jako politykę firmową.

## Troubleshooting

### Panel nie otwiera się

Uruchom launcher z menu Start, sprawdź usługę `WorkRouter` w `services.msc` i upewnij się, że plik stanu usługi jest dostępny dla administratora. Nie otwieraj API bez tokenu; odpowiedź `401` oznacza brak sesji, a nie uszkodzenie routera.

### Usługa zatrzymuje router

To zachowanie fail‑closed. Sprawdź bramki w panelu, zdarzenia, uprawnienia administratora i stan adaptera Wi‑Fi. Nie usuwaj ręcznie filtrów WFP w celu „odblokowania” pracy — najpierw uruchom diagnostykę.

### Laptop ma Wi‑Fi, ale nie ma Internetu

Potwierdź aktywny Ethernet, wyłącz inne połączenia upstream na czas testu, sprawdź bramkę WORK i uruchom walidację klienta. Zmiana pasma może chwilowo rozłączyć klientów.

### Udział SMB nie działa

Sprawdź, czy router działa, udział ma stan „gotowy”, folder istnieje i laptop korzysta z adresu bramy pokazanej w panelu. Usuń stare zapisane poświadczenia po stronie laptopa i wykonaj test poza firmowym VPN-em. Nie zapisuj hasła w skrypcie.

### Telemetria nie pokazuje domen

To może być prawidłowe. Sprawdź, czy inspekcja metadanych jest włączona, czy usługa działa oraz czy ruch przechodzi przez obserwowany interfejs. DoH, ECH, DoT, QUIC i VPN ograniczają widoczność, a ICS jest best‑effort.

### Walidacja zgłasza blokadę IPv6 lub LAN

Zweryfikuj, czy laptop nie korzysta równolegle z domowego Wi‑Fi, kabla, VPN-u albo innego adaptera. Zapisz wynik diagnostyki i powtórz test po wyłączeniu dodatkowych tras. Nie traktuj pojedynczego testu TCP jako dowodu pełnej izolacji wszystkich protokołów.

## Odinstalowanie

Uruchom PowerShell jako administrator:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\uninstall.ps1 -Confirm
```

Deinstalator prosi o potwierdzenie całej operacji, zatrzymuje router przez API, usuwa usługę, udział, konto techniczne, skróty i pliki programu oraz przywraca zapisaną wyjściową ACL folderu. Zawartość folderu `E:\Firmowe` pozostaje nienaruszona. Domyślnie przerywa pracę, jeśli nie potrafi potwierdzić bezpiecznego zatrzymania routera. Opcja `-AllowIncompleteRouterCleanup` jest awaryjna i może pozostawić filtry WFP — używaj jej wyłącznie po świadomej analizie. Przed odinstalowaniem skopiuj potrzebne pliki i odłącz laptop firmowy od WORK.

## Bezpieczeństwo i prywatność

- API jest związane z pętlą zwrotną i wymaga tokenu sesji;
- token jest przechowywany w chronionym stanie programu i nie powinien trafiać do repozytorium;
- pliki konfiguracyjne i hasło są chronione DPAPI komputera;
- konto udziału ma ograniczone ACL i blokady logowania interaktywnego;
- WFP działa fail‑closed, a watchdog reaguje na utratę ochrony;
- WorkRouter nie deszyfruje, nie modyfikuje i nie zapisuje treści HTTPS;
- telemetria jest lokalna, ulotna i ograniczona do metadanych;
- każdą zmianę polityki firmowego laptopa pozostawiamy administratorowi tej organizacji.

Zgłaszanie podatności opisano w [SECURITY.md](SECURITY.md).

## Status projektu

Projekt ma działający lokalny build, pakietowanie, usługę Windows, launcher, panel i testy core. Integracja z rzeczywistym sprzętem pozostaje osobnym etapem akceptacji: należy potwierdzić działanie Mobile Hotspot na konkretnym adapterze, przepływ przez ICS, WFP, SMB, IPv6 oraz zachowanie z firmowym VPN/EDR.

Nie oznaczamy projektu jako gotowego do produkcji wyłącznie na podstawie testów jednostkowych, mocków, zrzutów panelu lub poprawnego builda. Przed użyciem służbowym wykonaj walidację na fizycznym laptopie i uzyskaj zgodę właściciela polityki bezpieczeństwa.

## Współpraca i licencja

Zasady pracy nad zmianami opisuje [CONTRIBUTING.md](CONTRIBUTING.md), historię zmian zawiera [CHANGELOG.md](CHANGELOG.md), a licencja znajduje się w [LICENSE](LICENSE).
