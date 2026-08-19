(async function () {
  "use strict";
  const state = document.querySelector("#bootstrapState");
  const token = decodeURIComponent(location.hash.slice(1));
  history.replaceState(null, "", "/bootstrap.html");
  if (!token) {
    state.textContent = "Brak tokenu sesji. Otwórz panel z aplikacji WorkRouter.";
    return;
  }

  try {
    const response = await fetch("/api/session", {
      method: "POST",
      headers: { "X-WorkRouter-Token": token }
    });
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    location.replace("/");
  } catch {
    state.textContent = "Nie udało się ustanowić sesji. Uruchom panel ponownie z aplikacji WorkRouter.";
  }
}());
