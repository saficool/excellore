function BrandMark() {
  return (
    <span className="shell-brand-mark" aria-hidden="true">
      <span />
      <span />
      <span />
    </span>
  );
}

function ArrowIcon() {
  return <span aria-hidden="true">→</span>;
}

export function PublicHome({ joinHref }: { joinHref: string }) {
  return (
    <div className="shell-public">
      <a className="shell-public__skip" href="#main-content">
        Skip to content
      </a>
      <header className="shell-public__header">
        <a className="shell-public__brand" href="/" aria-label="Excellore home">
          <BrandMark />
          <span>Excellore</span>
        </a>
        <nav aria-label="Primary navigation">
          <a href="#platform">Platform</a>
          <a href="#experience">Experience</a>
          <a href="#security">Security</a>
        </nav>
        <div className="shell-public__actions">
          <a
            className="shell-button shell-button--quiet"
            href="/auth/login?returnTo=%2F"
          >
            Sign in
          </a>
          <a className="shell-button shell-button--light" href={joinHref}>
            Join with an invite
          </a>
        </div>
      </header>

      <main id="main-content">
        <section className="shell-hero" aria-labelledby="hero-heading">
          <div className="shell-hero__copy">
            <p className="shell-kicker">
              <span /> One identity. Every workspace.
            </p>
            <h1 id="hero-heading">
              Work moves better when everything feels connected.
            </h1>
            <p className="shell-hero__lead">
              Excellore brings your organizations, apps, and access together in
              one calm, secure place—so people can focus on the work that
              matters.
            </p>
            <div className="shell-hero__actions">
              <a
                className="shell-button shell-button--primary"
                href="/auth/login?returnTo=%2F"
              >
                Sign in to Excellore <ArrowIcon />
              </a>
              <a className="shell-text-link" href={joinHref}>
                Have an invitation? Join here <ArrowIcon />
              </a>
            </div>
            <ul className="shell-trust-list" aria-label="Platform highlights">
              <li>One secure sign-in</li>
              <li>Clear workspace context</li>
              <li>Access that follows your role</li>
            </ul>
          </div>

          <div
            className="shell-product-preview"
            aria-label="Excellore workspace preview"
          >
            <div className="shell-product-preview__glow" />
            <div className="shell-preview-window">
              <div className="shell-preview-topbar">
                <span className="shell-preview-logo">
                  <BrandMark />
                </span>
                <span className="shell-preview-search">
                  Search your workspace
                </span>
                <span className="shell-preview-person">OO</span>
              </div>
              <div className="shell-preview-body">
                <aside>
                  <span className="is-active">Overview</span>
                  <span>My workspaces</span>
                  <span>Invitations</span>
                  <span>Account</span>
                </aside>
                <div className="shell-preview-content">
                  <p>GOOD MORNING, OLIVIA</p>
                  <h2>Your work, ready when you are.</h2>
                  <div className="shell-preview-stats">
                    <span>
                      <strong>2</strong> Workspaces
                    </span>
                    <span>
                      <strong>2</strong> Available apps
                    </span>
                  </div>
                  <div className="shell-preview-grid">
                    <article>
                      <span className="shell-preview-icon shell-preview-icon--violet">
                        P
                      </span>
                      <strong>Profile</strong>
                      <small>Your account and directory</small>
                    </article>
                    <article>
                      <span className="shell-preview-icon shell-preview-icon--aqua">
                        A
                      </span>
                      <strong>Access</strong>
                      <small>People, roles, and permissions</small>
                    </article>
                  </div>
                </div>
              </div>
            </div>
          </div>
        </section>

        <section
          className="shell-proof"
          aria-label="Excellore platform principles"
        >
          <p>Designed for organizations that value</p>
          <div>
            <span>Clarity</span>
            <span>Control</span>
            <span>Trust</span>
            <span>Momentum</span>
          </div>
        </section>

        <section id="platform" className="shell-feature-section">
          <div className="shell-section-intro">
            <p className="shell-kicker">
              <span /> The Excellore platform
            </p>
            <h2>A consistent way to work across every organization.</h2>
            <p>
              Move between workspaces without losing context. Excellore keeps
              identity, access, and the apps you need easy to understand.
            </p>
          </div>
          <div className="shell-feature-grid">
            <article>
              <span className="shell-feature-number">01</span>
              <h3>One identity</h3>
              <p>
                Sign in once and move between the organizations you belong to.
              </p>
            </article>
            <article>
              <span className="shell-feature-number">02</span>
              <h3>Focused workspaces</h3>
              <p>
                See the right apps and information for the workspace you chose.
              </p>
            </article>
            <article>
              <span className="shell-feature-number">03</span>
              <h3>Access with purpose</h3>
              <p>
                Roles and permissions keep responsibilities visible and precise.
              </p>
            </article>
          </div>
        </section>

        <section id="experience" className="shell-split-section">
          <div className="shell-orbit" aria-hidden="true">
            <span className="shell-orbit__center">
              <BrandMark />
            </span>
            <span className="shell-orbit__node shell-orbit__node--one">ID</span>
            <span className="shell-orbit__node shell-orbit__node--two">P</span>
            <span className="shell-orbit__node shell-orbit__node--three">
              A
            </span>
          </div>
          <div>
            <p className="shell-kicker">
              <span /> A connected experience
            </p>
            <h2>Your workspaces travel with you.</h2>
            <p>
              Excellore gives every person one account and every organization
              its own secure context. Switching workspaces stays simple while
              boundaries stay clear.
            </p>
            <a className="shell-text-link" href="/auth/login?returnTo=%2F">
              Open your workspaces <ArrowIcon />
            </a>
          </div>
        </section>

        <section id="security" className="shell-security-section">
          <div>
            <p className="shell-kicker">
              <span /> Security by design
            </p>
            <h2>Trust is part of the experience.</h2>
          </div>
          <div className="shell-security-list">
            <article>
              <strong>Private browser sessions</strong>
              <p>
                Your browser receives a secure session, while access tokens stay
                server-side.
              </p>
            </article>
            <article>
              <strong>Tenant-aware access</strong>
              <p>
                Membership and permissions are checked for the workspace you
                open.
              </p>
            </article>
            <article>
              <strong>Visible context</strong>
              <p>
                You can always see which organization and app you are working
                in.
              </p>
            </article>
          </div>
        </section>

        <section className="shell-final-cta">
          <p className="shell-kicker">
            <span /> Ready when you are
          </p>
          <h2>Your Excellore workspace is one sign-in away.</h2>
          <div>
            <a
              className="shell-button shell-button--primary"
              href="/auth/login?returnTo=%2F"
            >
              Sign in <ArrowIcon />
            </a>
            <a className="shell-button shell-button--outline" href={joinHref}>
              Use an invitation
            </a>
          </div>
        </section>
      </main>

      <footer className="shell-public__footer">
        <a className="shell-public__brand" href="/">
          <BrandMark />
          <span>Excellore</span>
        </a>
        <p>One secure place for every workspace.</p>
        <span>© {new Date().getUTCFullYear()} Excellore</span>
      </footer>
    </div>
  );
}
