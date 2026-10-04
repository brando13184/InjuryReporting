// Cap the date picker at "today" (the server re-validates). Done in script because inline attributes
// computed server-side would be cacheable; the field is a native calendar picker (<input type="date">).
document.querySelectorAll('input[type="date"][data-max-today]').forEach(function (el) {
    var d = new Date();
    var m = String(d.getMonth() + 1).padStart(2, '0');
    var day = String(d.getDate()).padStart(2, '0');
    el.max = d.getFullYear() + '-' + m + '-' + day;
});

// Live character counter for the narrative box.
document.querySelectorAll('textarea[data-counter]').forEach(function (ta) {
    var target = document.getElementById(ta.getAttribute('data-counter'));
    if (!target) return;
    var update = function () { target.textContent = ta.value.length + ' / ' + ta.maxLength; };
    ta.addEventListener('input', update);
    update();
});
