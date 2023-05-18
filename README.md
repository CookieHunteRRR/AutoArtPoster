# AutoArtPoster

Скрипт, позволяющий загружать изображения в ВК как отложенные посты в группе

https://oauth.vk.com/authorize?client_id={}&display=page&redirect_uri=https://oauth.vk.com/blank.html&scope=groups,wall,photos&response_type=token&v=5.131
<br>В этой ссылке client_id - ID созданного в ВК приложения

В keys.json:<br>
token - получаемый из ссылки выше токен<br>
groupId - ID группы, в которую будут загружаться изображения<br>
v - версия API ВК<br>