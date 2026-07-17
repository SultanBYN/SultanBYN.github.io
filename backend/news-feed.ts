import {
  DOMParser,
  Element,
} from "https://deno.land/x/deno_dom@v0.1.48/deno-dom-wasm.ts";

type NewsItemType = "Blog" | "News" | "Article";
type SourceKind = "devto" | "rss" | "atom" | "hackernews";

type SourceDefinition = {
  key: string;
  name: string;
  type: NewsItemType;
  kind: SourceKind;
  url: string;
};

type NewsFeedItem = {
  id: string;
  title: string;
  summary: string;
  sourceName: string;
  sourceUrl: string;
  itemUrl: string;
  author: string;
  publishedAt: string;
  updatedAt: string | null;
  type: NewsItemType;
  technologyTags: string[];
  matchedKeywords: string[];
};

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers":
    "authorization, x-client-info, apikey, content-type",
  "Access-Control-Allow-Methods": "GET, OPTIONS",
};

const sources: SourceDefinition[] = [
  {
    key: "devto",
    name: "dev.to",
    type: "Article",
    kind: "devto",
    url: "https://dev.to/api/articles?per_page=30&page=1",
  },
  {
    key: "dotnet-blog",
    name: ".NET Blog",
    type: "Blog",
    kind: "rss",
    url: "https://devblogs.microsoft.com/dotnet/feed/",
  },
  {
    key: "github-news",
    name: "dotnet/core News",
    type: "News",
    kind: "atom",
    url: "https://github.com/dotnet/core/discussions/categories/news.atom",
  },
  {
    key: "github-general",
    name: "dotnet/core General",
    type: "News",
    kind: "atom",
    url: "https://github.com/dotnet/core/discussions/categories/general.atom",
  },
  {
    key: "hacker-news",
    name: "Hacker News",
    type: "News",
    kind: "hackernews",
    url: "https://hacker-news.firebaseio.com/v0/topstories.json",
  },
  {
    key: "the-hacker-news",
    name: "The Hacker News",
    type: "News",
    kind: "rss",
    url: "https://feeds.feedburner.com/TheHackersNews",
  },
];

const ecosystemKeywords = [
  ".net",
  "dotnet",
  "asp.net",
  "aspnet",
  "ef core",
  "entity framework",
  "blazor",
  "c#",
  "minimal api",
  "minimal apis",
  "maui",
  "nuget",
  "signalr",
  "identity",
  "azure",
  "open telemetry",
  "linq",
];

const textCleaner = /<[^>]+>/g;
const whitespaceRe = /\s+/g;

const xmlParser = new DOMParser();

Deno.serve(async (req) => {
  if (req.method === "OPTIONS") {
    return new Response("ok", { headers: corsHeaders });
  }

  if (req.method !== "GET") {
    return jsonResponse(
      { items: [], lastRefreshed: null, errorMessage: "Method not allowed" },
      405,
    );
  }

  try {
    const items = await loadItems();
    return jsonResponse(
      {
        items,
        lastRefreshed: new Date().toISOString(),
        errorMessage: null,
      },
      200,
    );
  } catch (error) {
    console.error("news-feed function failed", error);
    return jsonResponse(
      {
        items: [],
        lastRefreshed: new Date().toISOString(),
        errorMessage: "Unable to load the news feed right now.",
      },
      500,
    );
  }
});

async function loadItems(): Promise<NewsFeedItem[]> {
  const batches = await Promise.all(
    sources.map((source) => loadSource(source)),
  );

  return batches
    .flat()
    .filter(
      (item, index, list) =>
        index === list.findIndex((candidate) => candidate.id === item.id),
    )
    .sort((a, b) => Date.parse(b.publishedAt) - Date.parse(a.publishedAt));
}

async function loadSource(source: SourceDefinition): Promise<NewsFeedItem[]> {
  try {
    switch (source.kind) {
      case "devto":
        return await loadDevTo(source);
      case "rss":
        return await loadXmlFeed(source, "rss");
      case "atom":
        return await loadXmlFeed(source, "feed");
      case "hackernews":
        return await loadHackerNews(source);
    }
  } catch (error) {
    console.warn(`Failed to load ${source.name}`, error);
  }

  return [];
}

async function loadDevTo(source: SourceDefinition): Promise<NewsFeedItem[]> {
  const response = await fetch(source.url, {
    headers: { accept: "application/json" },
  });
  if (!response.ok) {
    return [];
  }

  const articles = (await response.json()) as Array<Record<string, unknown>>;
  const items: NewsFeedItem[] = [];

  for (const article of articles) {
    const title = getString(article.title);
    const description = getString(article.description);
    const itemUrl = getString(article.url) || getString(article.canonical_url);
    const author = getNestedString(article.user, "name");
    const publishedAt =
      getString(article.published_at) || getString(article.created_at);
    const updatedAt = getString(article.edited_at);
    const tagList = getStringArray(article.tag_list);
    const technologyTags = inferTechnologyTags(
      [title, description, tagList.join(" ")].join(" "),
    );
    const matchedKeywords = findKeywords(
      [title, description, technologyTags.join(" ")].join(" "),
    );

    if (!isRelevant(matchedKeywords)) {
      continue;
    }

    items.push(
      createItem(source, {
        id: `${source.key}:${itemUrl || title}`,
        title,
        summary: cleanText(description),
        itemUrl: itemUrl || source.url,
        author,
        publishedAt,
        updatedAt,
        technologyTags,
        matchedKeywords,
      }),
    );
  }

  return items;
}

async function loadXmlFeed(
  source: SourceDefinition,
  expectedRoot: "rss" | "feed",
): Promise<NewsFeedItem[]> {
  const response = await fetch(source.url, {
    headers: { accept: "application/xml,text/xml,*/*" },
  });
  if (!response.ok) {
    return [];
  }

  const xml = await response.text();
  const document = xmlParser.parseFromString(xml, "text/html");
  if (!document) {
    return [];
  }

  const rootEl = document.querySelector(expectedRoot);
  if (!rootEl) {
    return [];
  }

  return expectedRoot === "rss"
    ? parseRssFeed(source, document)
    : parseAtomFeed(source, document);
}

function parseRssFeed(
  source: SourceDefinition,
  document: ReturnType<DOMParser["parseFromString"]>,
): NewsFeedItem[] {
  const channel = document!.querySelector("channel");
  const sourceUrl =
    cleanText(channel?.querySelector("link")?.textContent) || source.url;
  const items: NewsFeedItem[] = [];

  for (const node of Array.from(document!.querySelectorAll("item"))) {
    const el = node as Element;
    const title = cleanText(el.querySelector("title")?.textContent);
    const summary = cleanText(
      el.querySelector("description")?.textContent ||
        el.querySelector("summary")?.textContent,
    );
    const itemUrl =
      cleanText(el.querySelector("link")?.textContent) ||
      cleanText(el.querySelector("guid")?.textContent) ||
      sourceUrl;
    const author =
      cleanText(findChildByTagName(el, "creator")?.textContent) ||
      cleanText(el.querySelector("author")?.textContent) ||
      "Unknown";
    const publishedAt = cleanText(
      el.querySelector("pubDate")?.textContent ||
        el.querySelector("published")?.textContent,
    );
    const updatedAt = cleanText(el.querySelector("updated")?.textContent);
    const extractedTags = extractTags(el);
    const technologyTags = inferTechnologyTags(
      [title, summary, extractedTags.join(" ")].join(" "),
    );
    const matchedKeywords = findKeywords(
      [title, summary, technologyTags.join(" ")].join(" "),
    );

    if (!title || !isRelevant(matchedKeywords)) {
      continue;
    }

    items.push(
      createItem(source, {
        id: `${source.key}:${itemUrl || title}`,
        title,
        summary,
        itemUrl,
        author,
        publishedAt,
        updatedAt,
        technologyTags,
        matchedKeywords,
        sourceUrl,
      }),
    );
  }

  return items;
}

function parseAtomFeed(
  source: SourceDefinition,
  document: ReturnType<DOMParser["parseFromString"]>,
): NewsFeedItem[] {
  const feedLinks = Array.from(document!.querySelectorAll("feed > link"));
  const altLink = feedLinks.find(
    (l) => (l as Element).getAttribute("rel") === "alternate",
  ) as Element | undefined;
  const firstLink = feedLinks[0] as Element | undefined;
  const sourceUrl =
    cleanText(
      altLink?.getAttribute("href") || firstLink?.getAttribute("href"),
    ) || source.url;
  const items: NewsFeedItem[] = [];

  for (const node of Array.from(document!.querySelectorAll("entry"))) {
    const el = node as Element;
    const title = cleanText(el.querySelector("title")?.textContent);
    const summary = cleanText(
      el.querySelector("summary")?.textContent ||
        el.querySelector("content")?.textContent,
    );
    const entryLinks = Array.from(el.querySelectorAll("link"));
    const entryAltLink = entryLinks.find(
      (l) => (l as Element).getAttribute("rel") === "alternate",
    ) as Element | undefined;
    const entryFirstLink = entryLinks[0] as Element | undefined;
    const itemUrl =
      cleanText(
        entryAltLink?.getAttribute("href") ||
          entryFirstLink?.getAttribute("href"),
      ) || sourceUrl;
    const authorEl = el.querySelector("author");
    const author =
      cleanText(
        authorEl?.querySelector("name")?.textContent || authorEl?.textContent,
      ) || "Unknown";
    const publishedAt = cleanText(
      el.querySelector("published")?.textContent ||
        el.querySelector("updated")?.textContent,
    );
    const updatedAt = cleanText(el.querySelector("updated")?.textContent);
    const extractedTags = extractTags(el);
    const technologyTags = inferTechnologyTags(
      [title, summary, extractedTags.join(" ")].join(" "),
    );
    const matchedKeywords = findKeywords(
      [title, summary, technologyTags.join(" ")].join(" "),
    );

    if (!title || !isRelevant(matchedKeywords)) {
      continue;
    }

    items.push(
      createItem(source, {
        id: `${source.key}:${itemUrl || title}`,
        title,
        summary,
        itemUrl,
        author,
        publishedAt,
        updatedAt,
        technologyTags,
        matchedKeywords,
        sourceUrl,
      }),
    );
  }

  return items;
}

async function loadHackerNews(
  source: SourceDefinition,
): Promise<NewsFeedItem[]> {
  const response = await fetch(source.url, {
    headers: { accept: "application/json" },
  });
  if (!response.ok) {
    return [];
  }

  const ids = (await response.json()) as number[];
  const topStories = ids.slice(0, 30);
  const storyResponses = await Promise.all(
    topStories.map((id) => fetchHackerNewsItem(id)),
  );

  return storyResponses
    .filter(
      (story): story is Record<string, unknown> => story !== null,
    )
    .map((story) => {
      const title = getString(story.title);
      const text = cleanText(getString(story.text));
      const itemUrl =
        getString(story.url) ||
        `https://news.ycombinator.com/item?id=${getString(story.id)}`;
      const author = getString(story.by) || "Unknown";
      const publishedAt = unixTimeToIso(getNumber(story.time));
      const technologyTags = inferTechnologyTags([title, text].join(" "));
      const matchedKeywords = findKeywords(
        [title, text, technologyTags.join(" ")].join(" "),
      );

      if (!title || !isRelevant(matchedKeywords)) {
        return null;
      }

      return createItem(source, {
        id: `${source.key}:${getString(story.id)}`,
        title,
        summary: text || title,
        itemUrl,
        author,
        publishedAt,
        updatedAt: null,
        technologyTags,
        matchedKeywords,
      });
    })
    .filter((item): item is NewsFeedItem => item !== null);
}

async function fetchHackerNewsItem(
  id: number,
): Promise<Record<string, unknown> | null> {
  const response = await fetch(
    `https://hacker-news.firebaseio.com/v0/item/${id}.json`,
  );
  if (!response.ok) {
    return null;
  }

  return (await response.json()) as Record<string, unknown>;
}

function createItem(
  source: SourceDefinition,
  item: {
    id: string;
    title: string;
    summary: string;
    itemUrl: string;
    author: string;
    publishedAt: string;
    updatedAt: string | null;
    technologyTags: string[];
    matchedKeywords: string[];
    sourceUrl?: string;
  },
): NewsFeedItem {
  return {
    id: item.id,
    title: item.title,
    summary: item.summary,
    sourceName: source.name,
    sourceUrl: item.sourceUrl || source.url,
    itemUrl: item.itemUrl,
    author: item.author || "Unknown",
    publishedAt: normalizeDate(item.publishedAt),
    updatedAt: item.updatedAt ? normalizeDate(item.updatedAt) : null,
    type: source.type,
    technologyTags: item.technologyTags,
    matchedKeywords: item.matchedKeywords,
  };
}

function inferTechnologyTags(text: string): string[] {
  const haystack = text.toLowerCase();
  const tags: string[] = [];

  for (const keyword of ecosystemKeywords) {
    if (haystack.includes(keyword)) {
      if (keyword === ".net" || keyword === "dotnet") {
        tags.push(".NET");
      } else if (keyword === "ef core" || keyword === "entity framework") {
        tags.push("EF Core");
      } else if (keyword === "asp.net" || keyword === "aspnet") {
        tags.push("ASP.NET");
      } else if (keyword === "open telemetry") {
        tags.push("OpenTelemetry");
      } else if (keyword === "minimal api" || keyword === "minimal apis") {
        tags.push("Minimal APIs");
      } else {
        tags.push(formatTag(keyword));
      }
    }
  }

  return Array.from(new Set(tags));
}

function findKeywords(text: string): string[] {
  const haystack = text.toLowerCase();
  return ecosystemKeywords.filter((keyword) => haystack.includes(keyword));
}

function isRelevant(matchedKeywords: string[]): boolean {
  return matchedKeywords.length > 0;
}

function findChildByTagName(parent: Element, localName: string): Element | null {
  for (const child of Array.from(parent.children)) {
    const tag = (child as Element).tagName?.toLowerCase() ?? "";
    if (tag === localName || tag.endsWith(`:${localName}`)) {
      return child as Element;
    }
  }
  return null;
}

function extractTags(node: Element): string[] {
  return Array.from(node.querySelectorAll("category"))
    .map((category) => {
      const el = category as Element;
      return cleanText(el.getAttribute("term") || el.textContent || "");
    })
    .filter(Boolean);
}

function formatTag(keyword: string): string {
  return keyword
    .split(/[\s.-]+/g)
    .filter(Boolean)
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
    .join(" ");
}

function cleanText(value: string | null | undefined): string {
  return (value || "")
    .replace(textCleaner, " ")
    .replace(whitespaceRe, " ")
    .trim();
}

function getString(value: unknown): string {
  return typeof value === "string" ? value : "";
}

function getNumber(value: unknown): number {
  return typeof value === "number" ? value : Number.NaN;
}

function getStringArray(value: unknown): string[] {
  return Array.isArray(value)
    ? value.filter((entry): entry is string => typeof entry === "string")
    : [];
}

function getNestedString(value: unknown, key: string): string {
  if (!value || typeof value !== "object") {
    return "";
  }

  const record = value as Record<string, unknown>;
  return getString(record[key]);
}

function unixTimeToIso(unixSeconds: number): string {
  if (!Number.isFinite(unixSeconds)) {
    return new Date().toISOString();
  }

  return new Date(unixSeconds * 1000).toISOString();
}

function normalizeDate(value: string): string {
  const parsed = Date.parse(value);
  return Number.isNaN(parsed) ? new Date().toISOString() : new Date(parsed).toISOString();
}

function jsonResponse(payload: unknown, status = 200): Response {
  return Response.json(payload, {
    status,
    headers: corsHeaders,
  });
}
