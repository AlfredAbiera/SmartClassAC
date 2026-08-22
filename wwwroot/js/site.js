(function () {
    "use strict";

    function getMenuElements() {
        return {
            menu: document.getElementById("smartMobileMenu"),
            button: document.querySelector(".mobile-menu-button"),
            icon: document.querySelector(".mobile-menu-button i")
        };
    }

    function closeSmartMenu() {
        const { menu, button, icon } = getMenuElements();
        menu?.classList.remove("show");
        button?.setAttribute("aria-expanded", "false");
        icon?.classList.remove("fa-xmark");
        icon?.classList.add("fa-bars");
    }

    window.toggleSmartMenu = function () {
        const { menu, button, icon } = getMenuElements();
        if (!menu) return;

        const isOpen = menu.classList.toggle("show");
        button?.setAttribute("aria-expanded", String(isOpen));
        icon?.classList.toggle("fa-bars", !isOpen);
        icon?.classList.toggle("fa-xmark", isOpen);
    };

    function setActiveNavigation(target) {
        document.querySelectorAll(".smart-nav-link, .mobile-nav-link").forEach(link => {
            const isActive = link.getAttribute("href") === target;
            link.classList.toggle("active", isActive);

            if (isActive) {
                link.setAttribute("aria-current", "page");
            } else {
                link.removeAttribute("aria-current");
            }
        });
    }

    function updateActiveNavigation() {
        if (!document.querySelector(".smart-navbar")) return;

        if (window.scrollY < 300) {
            setActiveNavigation("/");
            return;
        }

        const activeSection = [...document.querySelectorAll("section[id]")]
            .find(section => {
                const top = section.offsetTop - 180;
                return window.scrollY >= top && window.scrollY < top + section.offsetHeight;
            });

        if (activeSection) setActiveNavigation(`#${activeSection.id}`);
    }

    function startCounters() {
        document.querySelectorAll(".stat-number:not([data-animated])").forEach(counter => {
            const target = Number(counter.dataset.count);
            if (!Number.isFinite(target)) return;

            counter.dataset.animated = "true";
            const animate = () => {
                const startedAt = performance.now();
                const duration = 1500;

                function frame(now) {
                    const progress = Math.min((now - startedAt) / duration, 1);
                    const eased = 1 - Math.pow(1 - progress, 3);
                    counter.textContent = String(Math.floor(eased * target));
                    if (progress < 1) requestAnimationFrame(frame);
                }

                requestAnimationFrame(frame);
            };

            if ("IntersectionObserver" in window) {
                const observer = new IntersectionObserver(entries => {
                    if (entries.some(entry => entry.isIntersecting)) {
                        animate();
                        observer.disconnect();
                    }
                }, { threshold: .5 });
                observer.observe(counter);
            } else {
                animate();
            }
        });
    }

    window.updateClassroomTemperature = function (value) {
        const temperature = Number(value);
        if (!Number.isFinite(temperature)) return;

        const message = temperature <= 20
            ? "Extra cooling for warm classrooms."
            : temperature >= 25
                ? "Energy-saving comfort mode selected."
                : "Balanced cooling for focused learning.";

        const labels = {
            targetTemperature: `${temperature}°C`,
            heroTemperature: String(temperature),
            cardTemperature: `${temperature}°C`,
            comfortMessage: message
        };

        Object.entries(labels).forEach(([id, text]) => {
            const element = document.getElementById(id);
            if (element) element.textContent = text;
        });
    };

    function initializeLandingPage() {
        startCounters();
        updateActiveNavigation();
    }

    document.addEventListener("click", event => {
        const link = event.target.closest('.smart-navbar a[href^="#"]');
        if (!link) return;

        const target = document.querySelector(link.getAttribute("href"));
        if (!target) return;

        event.preventDefault();
        setActiveNavigation(link.getAttribute("href"));
        closeSmartMenu();
        const navHeight = document.querySelector(".smart-navbar")?.offsetHeight ?? 0;
        window.scrollTo({ top: target.getBoundingClientRect().top + window.scrollY - navHeight - 20, behavior: "smooth" });
    });

    document.addEventListener("input", event => {
        if (event.target instanceof HTMLInputElement && event.target.id === "temperatureControl") {
            window.updateClassroomTemperature(event.target.value);
        }
    });

    window.addEventListener("scroll", updateActiveNavigation, { passive: true });
    document.addEventListener("DOMContentLoaded", initializeLandingPage);
    document.addEventListener("enhancedload", initializeLandingPage);
}());
