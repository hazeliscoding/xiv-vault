// The demo plays only while it is on screen, never for people who asked for less motion, and
// always has a pause button. The guide's contents follow the reader through the page.

(() => {
  const video = document.getElementById("demo");
  const toggle = document.querySelector("[data-demo-toggle]");
  if (video && toggle) {
    const reduced = window.matchMedia("(prefers-reduced-motion: reduce)");
    let pausedByUser = reduced.matches;
    const label = toggle.querySelector("[data-label]");
    const iconPause = toggle.querySelector("[data-icon-pause]");
    const iconPlay = toggle.querySelector("[data-icon-play]");

    const show = () => {
      const playing = !video.paused;
      label.textContent = playing ? "Pause" : "Play";
      iconPause.hidden = !playing;
      iconPlay.hidden = playing;
    };
    const play = () => video.play().catch(() => {}).finally(show);

    toggle.hidden = false;
    show();
    toggle.addEventListener("click", () => {
      pausedByUser = !video.paused;
      if (pausedByUser) {
        video.pause();
        show();
      } else {
        play();
      }
    });
    video.addEventListener("play", show);
    video.addEventListener("pause", show);

    new IntersectionObserver(([entry]) => {
      if (entry.isIntersecting && !pausedByUser) {
        play();
      } else if (!entry.isIntersecting) {
        video.pause();
      }
    }, { threshold: 0.25 }).observe(video);
  }

  const links = [...document.querySelectorAll(".toc a[href^='#']")];
  if (links.length) {
    const byId = new Map(links.map((link) => [link.hash.slice(1), link]));
    const sections = [...byId.keys()].map((id) => document.getElementById(id)).filter(Boolean);
    const mark = (id) => {
      for (const [key, link] of byId) {
        if (key === id) {
          link.setAttribute("aria-current", "location");
        } else {
          link.removeAttribute("aria-current");
        }
      }
    };
    const update = () => {
      // The current section is the last one whose heading has passed the header.
      let current = sections[0];
      for (const section of sections) {
        if (section.getBoundingClientRect().top - 120 <= 0) {
          current = section;
        }
      }
      mark(current.id);
    };
    document.addEventListener("scroll", update, { passive: true });
    update();
  }
})();
