// portfolio.js — Animations & Interop

window.portfolioInterop = {

    // ── Typing Effect ──────────────────────────────────────────
    _themeStorageKey: 'portfolio-theme',

    _getSystemTheme: function () {
        return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches
            ? 'dark'
            : 'light';
    },

    getTheme: function () {
        let storedTheme = null;

        try {
            storedTheme = localStorage.getItem(this._themeStorageKey);
        } catch (e) {
            storedTheme = null;
        }

        return storedTheme === 'light' || storedTheme === 'dark'
            ? storedTheme
            : this._getSystemTheme();
    },

    applyTheme: function (theme) {
        const normalizedTheme = theme === 'dark' ? 'dark' : 'light';
        document.documentElement.setAttribute('data-theme', normalizedTheme);
        document.documentElement.style.colorScheme = normalizedTheme;

        try {
            localStorage.setItem(this._themeStorageKey, normalizedTheme);
        } catch (e) {
            // Ignore storage failures so theme still applies in private browsing.
        }

        return normalizedTheme;
    },

    toggleTheme: function () {
        const nextTheme = this.getTheme() === 'dark' ? 'light' : 'dark';
        return this.applyTheme(nextTheme);
    },

    _typingIntervals: {},

    startTypingEffect: function (elementId, phrases, typingSpeed, deletingSpeed, pauseTime) {
        const el = document.getElementById(elementId);
        if (!el) return;

        if (this._typingIntervals[elementId]) {
            clearTimeout(this._typingIntervals[elementId]);
            delete this._typingIntervals[elementId];
        }

        let phraseIndex = 0;
        let charIndex = 0;
        let isDeleting = false;

        const type = () => {
            const current = phrases[phraseIndex];

            if (isDeleting) {
                el.textContent = current.substring(0, charIndex - 1);
                charIndex--;
            } else {
                el.textContent = current.substring(0, charIndex + 1);
                charIndex++;
            }

            let delay = isDeleting ? deletingSpeed : typingSpeed;

            if (!isDeleting && charIndex === current.length) {
                delay = pauseTime;
                isDeleting = true;
            } else if (isDeleting && charIndex === 0) {
                isDeleting = false;
                phraseIndex = (phraseIndex + 1) % phrases.length;
                delay = 400;
            }

            this._typingIntervals[elementId] = setTimeout(type, delay);
        };

        type();
    },

    stopTypingEffect: function (elementId) {
        if (elementId) {
            if (this._typingIntervals[elementId]) {
                clearTimeout(this._typingIntervals[elementId]);
                delete this._typingIntervals[elementId];
            }
            return;
        }

        Object.keys(this._typingIntervals).forEach(key => {
            clearTimeout(this._typingIntervals[key]);
            delete this._typingIntervals[key];
        });
    },

    // ── Scroll Reveal Observer ─────────────────────────────────
    _observer: null,

    initScrollReveal: function () {
        if (this._observer) this._observer.disconnect();

        this._observer = new IntersectionObserver((entries) => {
            entries.forEach(entry => {
                if (entry.isIntersecting) {
                    entry.target.classList.add('visible');
                }
            });
        }, {
            threshold: 0.1,
            rootMargin: '0px 0px -50px 0px'
        });

        document.querySelectorAll('.reveal, .stagger-children').forEach(el => {
            this._observer.observe(el);
        });
    },

    // ── Animated Counter ───────────────────────────────────────
    animateCounter: function (elementId, targetValue, duration) {
        const el = document.getElementById(elementId);
        if (!el) return;

        const start = 0;
        const startTime = performance.now();

        const easeOutQuart = t => 1 - Math.pow(1 - t, 4);

        const update = (currentTime) => {
            const elapsed = currentTime - startTime;
            const progress = Math.min(elapsed / duration, 1);
            const easedProgress = easeOutQuart(progress);
            const current = Math.round(start + (targetValue - start) * easedProgress);

            el.textContent = current + (el.dataset.suffix || '');

            if (progress < 1) {
                requestAnimationFrame(update);
            }
        };

        requestAnimationFrame(update);
    },

    // ── Scroll to Section ────────────────────────────────────────
    scrollToSection: function (sectionId) {
        const section = document.getElementById(sectionId);
        if (!section) return;
        section.scrollIntoView({
            behavior: 'smooth',
            block: 'start'
        });
    },

    scrollToTop: function () {
        window.scrollTo({ top: 0, behavior: 'smooth' });
    },

    // ── Scroll Spy — track active section ─────────────────────
    _scrollHandler: null,
    _resizeHandler: null,
    _scrollRAF: null,
    _activeSectionId: null,

    initScrollSpy: function () {
        if (this._scrollHandler) {
            window.removeEventListener('scroll', this._scrollHandler);
            this._scrollHandler = null;
        }

        if (this._resizeHandler) {
            window.removeEventListener('resize', this._resizeHandler);
            this._resizeHandler = null;
        }

        this._activeSectionId = null;

        const sections = Array.from(document.querySelectorAll('.scroll-section'));
        if (!sections.length) return;

        const sectionNames = {
            home: 'Home', about: 'About', experience: 'Experience',
            skills: 'Skills', projects: 'Projects',
            certifications: 'Certifications',
            news: 'News'
        };

        const setActiveSection = (activeId) => {
            if (!activeId || activeId === this._activeSectionId) return;

            this._activeSectionId = activeId;

            document.querySelectorAll('.nav-item').forEach(nav => {
                const isActive = nav.getAttribute('href') === '#' + activeId;
                nav.classList.toggle('active', isActive);

                if (isActive) {
                    nav.setAttribute('aria-current', 'page');
                } else {
                    nav.removeAttribute('aria-current');
                }
            });

            const statusValue = document.querySelector('.status-left .status-value');
            if (statusValue) {
                statusValue.textContent = sectionNames[activeId] || 'Portfolio';
            }

            const mobileSectionName = document.getElementById('mobileSectionName');
            if (mobileSectionName) {
                mobileSectionName.textContent = sectionNames[activeId] || 'Portfolio';
            }
        };

        const updateActiveSection = () => {
            const viewportAnchor = window.innerHeight * 0.35;
            let activeId = sections[0].id;

            for (const section of sections) {
                const rect = section.getBoundingClientRect();

                if (rect.top <= viewportAnchor && rect.bottom > 0) {
                    activeId = section.id;
                } else if (rect.top > viewportAnchor) {
                    break;
                }
            }

            setActiveSection(activeId);
        };

        this._scrollHandler = () => {
            if (this._scrollRAF) cancelAnimationFrame(this._scrollRAF);
            this._scrollRAF = requestAnimationFrame(updateActiveSection);
        };
        this._resizeHandler = this._scrollHandler;

        window.addEventListener('scroll', this._scrollHandler, { passive: true });
        window.addEventListener('resize', this._resizeHandler);

        updateActiveSection();
    },

    // ── Page Transition ────────────────────────────────────────
    triggerPageTransition: function () {
        const container = document.querySelector('.page-container');
        if (container) {
            container.style.opacity = '0';
            container.style.transform = 'translateY(15px)';
            requestAnimationFrame(() => {
                container.style.transition = 'opacity 400ms cubic-bezier(0.16, 1, 0.3, 1), transform 400ms cubic-bezier(0.16, 1, 0.3, 1)';
                container.style.opacity = '1';
                container.style.transform = 'translateY(0)';
            });
        }
    }
};
