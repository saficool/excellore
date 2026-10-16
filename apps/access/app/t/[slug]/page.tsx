import { redirect } from 'next/navigation';

export default async function TenantIndex({
  params,
}: {
  params: Promise<{ slug: string }>;
}) {
  const { slug } = await params;
  redirect(`/t/${encodeURIComponent(slug)}/my-access`);
}
