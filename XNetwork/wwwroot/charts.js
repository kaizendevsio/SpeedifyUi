let charts=[];
export function initAll(ids){
    ids.forEach(id=>{
        const ctx=document.getElementById(id);
        charts.push(new Chart(ctx,{
            type:'line',
            data:{labels:Array(30).fill(''),
                datasets:[
                    {label:'↓ Mbps',data:Array(30).fill(0),tension:.3},
                    {label:'↑ Mbps',data:Array(30).fill(0),tension:.3}
                ]},
            options:{responsive:true,animation:false,plugins:{legend:{display:false}},
                scales:{y:{beginAtZero:true}}}
        }));
    });
}
export function updateAll(down,up){
    charts.forEach((c,i)=>{
        c.data.datasets[0].data.push(down[i]); c.data.datasets[0].data.shift();
        c.data.datasets[1].data.push(up[i]);   c.data.datasets[1].data.shift();
        c.update();
    });
}
