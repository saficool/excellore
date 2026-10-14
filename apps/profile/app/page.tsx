import Link from 'next/link';

export default function Home() {
  return (
    <main>
      <div className="ex-card">
        <h1 className="text-3xl font-semibold">Excellore Profile</h1>
        <p className="mt-4">Sign in with your Excellore account.</p>
        <Link
          className="mt-6 inline-block"
          href="/auth/login?returnTo=%2Faccount"
        >
          Sign in
        </Link>
      </div>
    </main>
  );
}
