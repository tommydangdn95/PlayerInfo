import { categoryLabel, confidenceMeta, tierLabel, topicEmoji } from '../types';

export function TopicTag({ topic }: { topic: string }) {
  return (
    <span className={`tag tag-topic tag-${topic.toLowerCase()}`}>
      {topicEmoji(topic)} {categoryLabel(topic)}
    </span>
  );
}

export function ConfidenceBadge({ confidence }: { confidence: string }) {
  const meta = confidenceMeta(confidence);
  return (
    <span className={`tag ${meta.className}`}>
      {meta.emoji} {meta.label}
    </span>
  );
}

export function TierBadge({ tier }: { tier: number }) {
  return <span className={`tier tier-${tier}`}>{tierLabel(tier)}</span>;
}

export function TeamCrest({ name, logoUrl, size = 40 }: { name: string; logoUrl: string | null; size?: number }) {
  return logoUrl ? (
    <img className="crest" src={logoUrl} alt="" loading="lazy" style={{ width: size, height: size }} />
  ) : (
    <span className="crest crest-empty" style={{ width: size, height: size }}>
      {name.charAt(0)}
    </span>
  );
}
