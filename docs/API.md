# Lokalne API WorkRouter

API jest interfejsem wewnętrznym panelu i launchera. Domyślnie nasłuchuje wyłącznie na `127.0.0.1:17437`; nie należy wystawiać go na Ethernet ani segment WORK. Wszystkie ścieżki `/api/*` wymagają nagłówka `X-WorkRouter-Token` lub prawidłowej lokalnej sesji utworzonej przez krótkotrwały bilet bootstrap.

## Odczyt stanu

| Metoda | Ścieżka | Zastosowanie |
| --- | --- | --- |
| `GET` | `/api/status` | Stan routera, bramki WFP/SMB, pasmo, transfer i konfiguracja panelu. |
| `GET` | `/api/clients` | Bieżący, ograniczony widok klientów WORK i ich użycia. |
| `GET` | `/api/events?afterId=` | Zdarzenia operacyjne nowsze niż podany identyfikator. |
| `GET` | `/api/preferences` | Preferencje telemetrii, autostartu routera i otwierania panelu. |
| `GET` | `/api/traffic/summary?windowMinutes=` | Agregaty za ograniczone okno czasowe. |
| `GET` | `/api/traffic/events?afterId=` | Ulotne metadane przepływów bez payloadu. |

## Operacje

| Metoda | Ścieżka | Efekt |
| --- | --- | --- |
| `POST` | `/api/router/start` | Uruchamia sekwencję kwarantanna → hotspot → aktywna izolacja. |
| `POST` | `/api/router/stop` | Zatrzymuje hotspot przed usunięciem filtrów. |
| `PUT` | `/api/settings` | Aktualizuje Wi-Fi i wykonuje kontrolowany restart/rollback, jeśli jest wymagany. |
| `PUT` | `/api/preferences` | Zapisuje preferencje lokalne i zarządza skrótem autostartu panelu. |
| `POST` | `/api/clients/primary` | Oznacza klienta głównego do prezentacji użycia. |
| `POST` | `/api/share/rotate-password` | Ponownie synchronizuje konto udziału z bieżącym hasłem Wi-Fi. |
| `POST` | `/api/traffic/clear` | Czyści ulotną historię telemetrii. |
| `POST` | `/api/diagnostics` | Uruchamia lokalną kontrolę bramek bez udawania testu fizycznego klienta. |

## Bootstrap i bezpieczeństwo

Launcher odczytuje chroniony plik endpointu, żąda biletu przez `POST /api/bootstrap-ticket`, a następnie otwiera jednorazową ścieżkę bootstrap w przeglądarce. Bilet nie jest trwałym tokenem API. Nie kopiuj tokenu, cookie, odpowiedzi statusu zawierającej hasło ani danych telemetrycznych do issue lub logów CI.

Odpowiedź `401` jest oczekiwanym wynikiem żądania bez autoryzacji. Błędy konfiguracji pasma mogą zwrócić `409` wraz z informacją o rollbacku; klient powinien wtedy ponownie pobrać stan zamiast zakładać powodzenie.

To API nie ma gwarancji kompatybilności dla klientów innych niż dostarczony panel i launcher w linii `0.x`.
