<script setup lang="ts">
// 维基引言引用块：页首引言，带大引号装饰
// 纯展示；文本为空时整块不渲染（不占位、不编造）
import { computed } from 'vue'

const props = defineProps<{
  /** 引言文本；空/纯空白时不渲染 */
  text?: string
  /** 引言署名（出处/说话人），可缺省 */
  attribution?: string
}>()

const hasText = computed(() => (props.text ?? '').trim() !== '')
</script>

<template>
  <blockquote v-if="hasText" class="wiki-quote">
    <p class="wiki-quote-text">{{ text }}</p>
    <footer v-if="attribution" class="wiki-quote-attribution">—— {{ attribution }}</footer>
    <span class="wiki-quote-mark" aria-hidden="true">”</span>
  </blockquote>
</template>

<style scoped>
.wiki-quote {
  position: relative;
  margin: 0 0 var(--lme-gap-xl);
  padding: var(--lme-gap-lg) var(--lme-gap-xl);
  background: var(--wiki-quote-bg);
  border-left: 4px solid var(--wiki-quote-border);
  border-radius: 0 var(--lme-radius-lg) var(--lme-radius-lg) 0;
}

.wiki-quote-text {
  margin: 0;
  font-size: var(--lme-font-size-lg);
  line-height: 1.8;
  color: var(--wiki-quote-text);
}

.wiki-quote-attribution {
  margin-top: var(--lme-gap-sm);
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-muted);
}

.wiki-quote-mark {
  position: absolute;
  right: var(--lme-gap-md);
  bottom: -12px;
  font-size: 64px;
  line-height: 1;
  font-family: Georgia, serif;
  color: var(--wiki-quote-mark);
  pointer-events: none;
  user-select: none;
}
</style>
